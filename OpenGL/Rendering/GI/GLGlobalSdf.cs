using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BallisticEngine;
using BallisticEngine.GI;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine.OpenGL.GI;

// LUMEN PHASE 1 — the GLOBAL DISTANCE FIELD (GDF), a clipmap of the MERGED scene SDF.
//
// Why this exists: the per-submesh SDF path (GLSdfAtlas + GLSdfScene) bakes a tight brick PER mesh
// and marches an instance grid. That works on connected/whole-mesh scenes (SunTemple) but on a
// fragmented per-object scene (BistroInterior: ~2000 separate props) the brick budget can't cover it,
// so the march escapes to nothing and the gather is ~0. Lumen's answer — and ours — is a GLOBAL field:
// ONE signed distance field over a world box around the camera, sampled directly. No per-object
// bricks, no instance loop, no cap, no fragmentation.
//
// Structure (matches Lumen's GDF): a CLIPMAP of N cascades centered on the camera. Each cascade is the
// SAME voxel resolution but covers 2x the world extent of the previous, so cascade 0 is fine near the
// camera and the outer cascades reach far at coarse detail. Each cascade snaps to its voxel grid and
// is re-baked only when the camera scrolls it past half a voxel (or the scene geometry changes).
//
// The bake is CPU-heavy (a BVH closest-triangle + 6-ray-parity sign per voxel), so it runs on a
// BACKGROUND task and uploads when done — never a GL-thread stall. The march samples the finest
// cascade containing a point with hardware trilinear (a global field has no slot-packing boundary, so
// linear filtering is safe, unlike the per-mesh atlas).
public sealed class GLGlobalSdf : IDisposable {
    public const int CascadeCount = 4;
    public int Resolution { get; }            // voxels per axis per cascade (cubic)
    public float BaseExtent { get; }          // cascade 0 world extent (metres, full box edge)

    // Per-cascade GPU + placement state.
    readonly int[] textures = new int[CascadeCount];   // R16F 3D distance fields (world metres, signed)
    readonly Vector3[] cascadeMin = new Vector3[CascadeCount];  // world-space min corner of each cascade box
    readonly float[] cascadeCell = new float[CascadeCount];     // world cell size (cubic) per cascade
    readonly bool[] cascadeBaked = new bool[CascadeCount];

    // VOXEL LIGHTING (Lumen Phase 2): a RADIANCE clipmap parallel to the distance clipmap — same
    // cascades, RGBA16F (rgb = lit radiance, a = surface occupancy). Each frame a compute pass lights
    // every near-surface voxel (sun + shadow + sky + one bounce from last frame), so a GDF hit reads
    // STABLE, COLORED, multi-bounce radiance here instead of the neutral direct estimate. Ping-pong
    // (read last frame, write this frame) avoids same-frame feedback — the per-mesh RadianceInject pattern.
    readonly int[] radianceA = new int[CascadeCount];  // RGBA16F radiance, ping-pong A
    readonly int[] radianceB = new int[CascadeCount];
    readonly int[] radianceRead = new int[CascadeCount]; // PER-cascade flag (we light one cascade/frame,
                                                         // so a global flag would desync the others).
    public int RadianceRead(int c) => radianceRead[c] == 0 ? radianceA[c] : radianceB[c];
    public int RadianceWrite(int c) => radianceRead[c] == 0 ? radianceB[c] : radianceA[c];
    void SwapRadiance(int c) => radianceRead[c] = 1 - radianceRead[c];

    public bool Available { get; private set; } // at least cascade 0 has been baked + uploaded once

    // Triangle snapshot + bake job state (background).
    sealed class BakeResult { public int Cascade; public MeshSdf Sdf; public Vector3 Min; public float Cell; }
    Task<BakeResult> bakeTask;
    int bakeCascade = -1;
    int geometryStamp;          // hash of the opaque set's transforms+bounds; re-bake all on change
    bool stampValid;

    // Voxel-lighting inject program (GlobalRadianceInject_Comp) + its cached uniform locations.
    int injectProgram;
    int liCascade, liCascadeMin, liCascadeCell, liRes, liSkyExposure, liFeedback;
    int liCascadeCountSun, liSunDir, liSunColor, liAlbedo, liCascadeBias;
    readonly int[] liCascadeMatrices = new int[4];
    readonly int[] liGdfMin = new int[CascadeCount];
    readonly int[] liGdfCell = new int[CascadeCount];
    int radianceInjectCursor; // round-robin: light one cascade per frame (amortize the dispatch cost)

    public GLGlobalSdf(int resolution = 64, float baseExtent = 16f) {
        Resolution = Math.Clamp(resolution, 16, 256);
        BaseExtent = MathF.Max(baseExtent, 1f);
        for (int c = 0; c < CascadeCount; c++) {
            textures[c] = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture3D, textures[c]);
            GL.TexStorage3D(TextureTarget3d.Texture3D, 1, SizedInternalFormat.R16f,
                Resolution, Resolution, Resolution);
            // Hardware trilinear is correct for the global field (no slot packing to bleed across).
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
            // Seed to a large positive distance so an un-baked cascade reads as empty (rays pass through)
            // rather than garbage. One float cleared via ClearTexImage.
            float far = BaseExtent * 4f;
            GL.ClearTexImage(textures[c], 0, PixelFormat.Red, PixelType.Float, ref far);

            radianceA[c] = CreateRadianceTexture();
            radianceB[c] = CreateRadianceTexture();
        }
        GL.BindTexture(TextureTarget.Texture3D, 0);

        injectProgram = CompileCompute(EmbeddedShaderSource.Read("GlobalRadianceInject_Comp.glsl"));
        if (injectProgram != 0)
            CacheInjectLocations();
    }

    static int CompileCompute(string src) {
        int sh = GL.CreateShader(ShaderType.ComputeShader);
        GL.ShaderSource(sh, GLSLShaderUtilities.ToAscii(src)); // em-dash sanitize (CLAUDE.md gotcha)
        GL.CompileShader(sh);
        GL.GetShader(sh, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0) {
            Debugging.LogError("[GLGlobalSdf] GlobalRadianceInject compile failed:\n" + GL.GetShaderInfoLog(sh));
            GL.DeleteShader(sh); return 0;
        }
        int prog = GL.CreateProgram();
        GL.AttachShader(prog, sh);
        GL.LinkProgram(prog);
        GL.GetProgram(prog, GetProgramParameterName.LinkStatus, out int lok);
        GL.DeleteShader(sh);
        if (lok == 0) {
            Debugging.LogError("[GLGlobalSdf] GlobalRadianceInject link failed:\n" + GL.GetProgramInfoLog(prog));
            GL.DeleteProgram(prog); return 0;
        }
        return prog;
    }

    void CacheInjectLocations() {
        liCascade = GL.GetUniformLocation(injectProgram, "Cascade");
        liCascadeMin = GL.GetUniformLocation(injectProgram, "CascadeMin");
        liCascadeCell = GL.GetUniformLocation(injectProgram, "CascadeCell");
        liRes = GL.GetUniformLocation(injectProgram, "Res");
        liSkyExposure = GL.GetUniformLocation(injectProgram, "SkyExposure");
        liFeedback = GL.GetUniformLocation(injectProgram, "Feedback");
        liCascadeCountSun = GL.GetUniformLocation(injectProgram, "CascadeCountSun");
        liSunDir = GL.GetUniformLocation(injectProgram, "SunDirectionWorld");
        liSunColor = GL.GetUniformLocation(injectProgram, "SunColor");
        liAlbedo = GL.GetUniformLocation(injectProgram, "Albedo");
        liCascadeBias = GL.GetUniformLocation(injectProgram, "CascadeBias");
        for (int i = 0; i < 4; i++)
            liCascadeMatrices[i] = GL.GetUniformLocation(injectProgram, $"CascadeMatrices[{i}]");
        for (int i = 0; i < CascadeCount; i++) {
            liGdfMin[i] = GL.GetUniformLocation(injectProgram, $"GdfMin[{i}]");
            liGdfCell[i] = GL.GetUniformLocation(injectProgram, $"GdfCell[{i}]");
        }
    }

    int CreateRadianceTexture() {
        int tex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture3D, tex);
        GL.TexStorage3D(TextureTarget3d.Texture3D, 1, SizedInternalFormat.Rgba16f, Resolution, Resolution, Resolution);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        GL.ClearTexImage(tex, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero); // start empty (a=0)
        return tex;
    }

    public int DistanceTexture(int c) => textures[c];

    // Per cascade: world extent doubles each level, centered on the camera, snapped to the voxel grid.
    float ExtentOf(int cascade) => BaseExtent * (1 << cascade);

    // Called each frame on the GL thread. Decides whether a cascade needs (re)baking — because it
    // scrolled past half a voxel since its last bake, or the scene geometry changed — and kicks ONE
    // background bake at a time, uploading the previous result if ready. Amortized: one cascade per
    // call, finest-first, so the near field comes up first and the cost spreads over a few frames.
    public void Update(Vector3 cameraPos, IReadOnlyList<IStaticMeshRenderer> opaque) {
        // Finish a completed background bake: upload it (GL thread).
        if (bakeTask is { IsCompleted: true }) {
            BakeResult r = bakeTask.Status == TaskStatus.RanToCompletion ? bakeTask.Result : null;
            if (r is { Sdf: not null }) {
                UploadCascade(r.Cascade, r.Sdf, r.Min, r.Cell);
                cascadeBaked[r.Cascade] = true;
                if (r.Cascade == 0) Available = true;
            }
            bakeTask = null;
            bakeCascade = -1;
        }
        if (bakeTask is not null)
            return; // a bake is in flight — one at a time keeps the BVH/grid cost bounded per frame

        // Geometry change => invalidate every cascade (re-bake from scratch).
        int stamp = GeometryStamp(opaque);
        if (!stampValid || stamp != geometryStamp) {
            geometryStamp = stamp;
            stampValid = true;
            for (int c = 0; c < CascadeCount; c++) cascadeBaked[c] = false;
        }

        // Pick the FINEST cascade that needs work: never baked, or the camera scrolled it past half a
        // voxel since its placement (clipmap scroll). Finest-first so the near field updates soonest.
        for (int c = 0; c < CascadeCount; c++) {
            float extent = ExtentOf(c);
            float cell = extent / Resolution;
            Vector3 snapped = Snap(cameraPos - new Vector3(extent * 0.5f), cell);
            bool needsBake = !cascadeBaked[c] ||
                (snapped - cascadeMin[c]).Length > cell * 0.5f;
            if (needsBake) {
                KickBake(c, snapped, cell, opaque);
                return;
            }
        }
    }

    static Vector3 Snap(Vector3 v, float cell) => new(
        MathF.Floor(v.X / cell) * cell, MathF.Floor(v.Y / cell) * cell, MathF.Floor(v.Z / cell) * cell);

    // Snapshot the opaque triangles overlapping this cascade's box (WORLD space) on the GL thread, then
    // bake the field on a background task (BVH + parallel grid). The snapshot is essential — the bake
    // can't touch engine state off-thread.
    void KickBake(int cascade, Vector3 min, float cell, IReadOnlyList<IStaticMeshRenderer> opaque) {
        float extent = cell * Resolution;
        Vector3 max = min + new Vector3(extent);
        // Pad the cull box by a couple cells so triangles just outside still seed the boundary field.
        Vector3 cullMin = min - new Vector3(cell * 2f);
        Vector3 cullMax = max + new Vector3(cell * 2f);

        var worldVerts = new List<Vector3>(4096);
        for (int i = 0; i < opaque.Count; i++) {
            IStaticMeshRenderer r = opaque[i];
            if (r is not { IsRenderable: true, IsActive: true })
                continue;
            Mesh mesh = r.SharedMesh;
            if (mesh?.Vertices is not { Length: > 0 } || mesh.Indices is not { Length: > 0 })
                continue;
            Transform t = r.Transform;
            if (t == null)
                continue;
            Matrix4 world = t.WorldMatrix;
            uint[] idx = mesh.Indices;
            Vector3[] verts = mesh.Vertices;
            // Which submeshes this renderer draws (whole-mesh = all; per-submesh = one).
            SubMeshData[] subs = mesh.SubMeshes;
            int subCount = subs?.Length ?? 0;
            int from = r.SubMeshIndex >= 0 ? r.SubMeshIndex : 0;
            int to = r.SubMeshIndex >= 0 ? r.SubMeshIndex + 1 : subCount;
            if (subCount == 0 || from < 0 || to > subCount)
                continue;
            for (int s = from; s < to; s++) {
                Material mat = r.MaterialFor(s);
                if (mat is { Cutout: true })
                    continue; // foliage/thin shells: excluded from SDF (Lumen handles via screen traces)
                SubMeshData sm = subs[s];
                int end = sm.IndexStart + sm.IndexCount;
                if (end > idx.Length)
                    continue;
                for (int ii = sm.IndexStart; ii + 2 < end; ii += 3) {
                    Vector3 a = Transform(verts[idx[ii]], world);
                    Vector3 b = Transform(verts[idx[ii + 1]], world);
                    Vector3 cc = Transform(verts[idx[ii + 2]], world);
                    // Triangle-vs-box reject: skip tris fully outside this cascade's padded box.
                    Vector3 tmin = Vector3.ComponentMin(a, Vector3.ComponentMin(b, cc));
                    Vector3 tmax = Vector3.ComponentMax(a, Vector3.ComponentMax(b, cc));
                    if (tmax.X < cullMin.X || tmin.X > cullMax.X ||
                        tmax.Y < cullMin.Y || tmin.Y > cullMax.Y ||
                        tmax.Z < cullMin.Z || tmin.Z > cullMax.Z)
                        continue;
                    worldVerts.Add(a); worldVerts.Add(b); worldVerts.Add(cc);
                }
            }
        }

        if (worldVerts.Count == 0) {
            // No geometry in this cascade — mark baked (the cleared far-distance field is correct: empty).
            cascadeBaked[cascade] = true;
            cascadeMin[cascade] = min;
            cascadeCell[cascade] = cell;
            if (cascade == 0) Available = true;
            return;
        }

        cascadeMin[cascade] = min;
        cascadeCell[cascade] = cell;
        var res = new Vector3i(Resolution);
        bakeCascade = cascade;
        bakeTask = Task.Run(() => new BakeResult {
            Cascade = cascade,
            Sdf = MeshSdfBaker.BakeWorldTriangles(worldVerts, min, max, res),
            Min = min, Cell = cell,
        });
    }

    static Vector3 Transform(Vector3 v, Matrix4 m) => (new Vector4(v, 1f) * m).Xyz;

    void UploadCascade(int cascade, MeshSdf sdf, Vector3 min, float cell) {
        cascadeMin[cascade] = min;
        cascadeCell[cascade] = cell;
        GL.BindTexture(TextureTarget.Texture3D, textures[cascade]);
        // x-fastest source matches MeshSdf.Index = x + Res.X*(y + Res.Y*z); R16F narrows from float.
        GL.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, 0,
            sdf.Res.X, sdf.Res.Y, sdf.Res.Z, PixelFormat.Red, PixelType.Float, sdf.Distances);
        GL.BindTexture(TextureTarget.Texture3D, 0);
    }

    // Cheap stamp of the opaque set: count + each renderer's world translation + bounds. Changes when
    // anything moves/appears/disappears, triggering a full re-bake (the field is dynamic-geometry-aware).
    static int GeometryStamp(IReadOnlyList<IStaticMeshRenderer> opaque) {
        var h = new HashCode();
        h.Add(opaque.Count);
        for (int i = 0; i < opaque.Count; i++) {
            Transform t = opaque[i].Transform;
            if (t == null) continue;
            Vector3 p = t.WorldMatrix.Row3.Xyz;
            h.Add((int)(p.X * 10)); h.Add((int)(p.Y * 10)); h.Add((int)(p.Z * 10));
        }
        return h.ToHashCode();
    }

    // VOXEL LIGHTING: light the radiance clipmap from the distance clipmap (sun+shadow+sky+one bounce).
    // Amortized — ONE cascade per call (round-robin), so the per-frame cost is bounded; the EMA keeps a
    // static view converging and a moving view refreshing over a few frames. Called by GLSdfGiPass.Render
    // (it has the sun/shadow/sky params), AFTER Update has placed/baked the cascades. Ping-pong: read
    // last frame's radiance (samplers), write this frame's, then SwapRadiance so the march reads fresh.
    public void InjectRadiance(int irradianceCubemap, int shadowMapArray, Matrix4[] sunCascades,
        Vector4 sunBias, int sunCascadeCount, Vector3 sunDir, Vector3 sunColor, Vector3 albedo,
        float skyExposure, float feedback) {
        if (!Available || injectProgram == 0)
            return;
        int c = radianceInjectCursor;
        radianceInjectCursor = (radianceInjectCursor + 1) % CascadeCount;
        if (!cascadeBaked[c])
            return;

        GL.UseProgram(injectProgram);
        // Write target: this cascade's WRITE radiance texture as image 1.
        GL.BindImageTexture(1, RadianceWrite(c), 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
        // This cascade's distance field (unit 0), sky (3), sun shadow (5).
        BindSampler(0, TextureTarget.Texture3D, textures[c]);
        BindSampler(3, TextureTarget.TextureCubeMap, irradianceCubemap);
        BindSampler(5, TextureTarget.Texture2DArray, shadowMapArray);
        // The whole GDF: distances on units 6..9, LAST frame's radiance on units 10..13 (for the bounce).
        for (int k = 0; k < CascadeCount; k++) {
            BindSampler(6 + k, TextureTarget.Texture3D, textures[k]);
            BindSampler(10 + k, TextureTarget.Texture3D, RadianceRead(k));
            GL.Uniform1(GL.GetUniformLocation(injectProgram, $"GdfDistance[{k}]"), 6 + k);
            GL.Uniform1(GL.GetUniformLocation(injectProgram, $"GdfRadiance[{k}]"), 10 + k);
            GL.Uniform3(liGdfMin[k], cascadeMin[k].X, cascadeMin[k].Y, cascadeMin[k].Z);
            GL.Uniform1(liGdfCell[k], cascadeCell[k]);
        }
        GL.Uniform1(GL.GetUniformLocation(injectProgram, "DistanceField"), 0);
        GL.Uniform1(GL.GetUniformLocation(injectProgram, "IrradianceMap"), 3);
        GL.Uniform1(GL.GetUniformLocation(injectProgram, "ShadowMap"), 5);

        GL.Uniform1(liCascade, c);
        GL.Uniform3(liCascadeMin, cascadeMin[c].X, cascadeMin[c].Y, cascadeMin[c].Z);
        GL.Uniform1(liCascadeCell, cascadeCell[c]);
        GL.Uniform1(liRes, Resolution);
        GL.Uniform1(liSkyExposure, skyExposure);
        GL.Uniform1(liFeedback, feedback);
        int sc = Math.Min(sunCascadeCount, 4);
        GL.Uniform1(liCascadeCountSun, sc);
        for (int i = 0; i < sc; i++)
            GL.UniformMatrix4(liCascadeMatrices[i], false, ref sunCascades[i]);
        GL.Uniform4(liCascadeBias, sunBias);
        GL.Uniform3(liSunDir, sunDir);
        GL.Uniform3(liSunColor, sunColor);
        GL.Uniform3(liAlbedo, albedo);

        int g = (Resolution + 3) / 4;
        GL.DispatchCompute(g, g, g);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);
        SwapRadiance(c); // only the cascade we just wrote flips (per-cascade ping-pong)
    }

    static void BindSampler(int unit, TextureTarget target, int texture) {
        GL.ActiveTexture(TextureUnit.Texture0 + unit);
        GL.BindTexture(target, texture);
    }

    // Bind the cascade textures to consecutive sampler units and report placement to the shader. The
    // caller sets the matching sampler uniforms + the cascade min/cell/res uniforms.
    public void Bind(int firstUnit) {
        for (int c = 0; c < CascadeCount; c++) {
            GL.ActiveTexture(TextureUnit.Texture0 + firstUnit + c);
            GL.BindTexture(TextureTarget.Texture3D, textures[c]);
        }
    }

    public Vector3 CascadeMin(int c) => cascadeMin[c];
    public float CascadeCell(int c) => cascadeCell[c];

    public void Dispose() {
        for (int c = 0; c < CascadeCount; c++) {
            if (textures[c] != 0) GL.DeleteTexture(textures[c]);
            if (radianceA[c] != 0) GL.DeleteTexture(radianceA[c]);
            if (radianceB[c] != 0) GL.DeleteTexture(radianceB[c]);
        }
        Available = false;
    }
}
