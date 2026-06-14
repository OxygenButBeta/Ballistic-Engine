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
    // Per-cascade baked DETAIL resolution (Phase A warm-up): 0 = never baked, CoarseResolution = a
    // fast low-res field is up (Lumen contributes immediately), Resolution = fully refined. A cascade
    // baked coarse is re-queued for a full bake so it sharpens over the next warm-up frames.
    readonly int[] cascadeRes = new int[CascadeCount];

    // WARM-UP: cascade 0 baked at full 96^3 over a big scene takes SECONDS (BVH closest-tri + sign per
    // ~884K voxels) — during which Lumen silently contributes nothing (just IBL ambient), now that the
    // legacy probe fallback is off. Fix: bake a COARSE field first (32^3 ≈ 33K voxels, ~27x fewer, up
    // in a fraction of a second), CPU-upsample it into the full-res texture so the march/placement are
    // unchanged, mark the cascade available, THEN re-bake it at full res in the background. So Lumen is
    // live within a frame or two with a slightly soft field that sharpens over the next ~second.
    const int CoarseResolution = 32;

    // VOXEL LIGHTING (Lumen Phase 2): a RADIANCE clipmap parallel to the distance clipmap — same
    // cascades, RGBA16F (rgb = lit radiance, a = surface occupancy). Each frame a compute pass lights
    // every near-surface voxel (sun + shadow + sky + one bounce from last frame), so a GDF hit reads
    // STABLE, COLORED, multi-bounce radiance here instead of the neutral direct estimate. Ping-pong
    // (read last frame, write this frame) avoids same-frame feedback — the per-mesh RadianceInject pattern.
    // Per-voxel ALBEDO clipmap (RGBA8, the nearest surface's material colour). The voxel-lighting inject
    // multiplies the bounce by THIS so a red wall bounces red (Lumen surface-cache albedo), not one grey.
    readonly int[] albedoTex = new int[CascadeCount];

    readonly int[] radianceA = new int[CascadeCount];  // RGBA16F radiance, ping-pong A
    readonly int[] radianceB = new int[CascadeCount];
    readonly int[] radianceRead = new int[CascadeCount]; // PER-cascade flag (we light one cascade/frame,
                                                         // so a global flag would desync the others).
    public int RadianceRead(int c) => radianceRead[c] == 0 ? radianceA[c] : radianceB[c];
    public int RadianceWrite(int c) => radianceRead[c] == 0 ? radianceB[c] : radianceA[c];
    void SwapRadiance(int c) => radianceRead[c] = 1 - radianceRead[c];

    public bool Available { get; private set; } // at least cascade 0 has been baked + uploaded once

    // Triangle snapshot + bake job state (background).
    sealed class BakeResult {
        public int Cascade; public MeshSdf Sdf; public float[] Albedo; public Vector3 Min; public float Cell;
        public int Res;        // detail resolution this bake produced (CoarseResolution or Resolution)
        public Vector3i SrcRes; // the field's own grid (== Res^3 unless degenerate) — for the upsample
        public MeshSdfBaker.PreparedField Prepared; // the built BVH, cached for this cascade's refine (Phase 0)
    }
    Task<BakeResult> bakeTask;
    int bakeCascade = -1;
    int geometryStamp;          // hash of the opaque set's transforms+bounds; re-bake all on change
    bool stampValid;

    // PHASE 0 (BVH reuse): a per-cascade PreparedField (built BVH + per-tri albedo over the cascade's
    // world-triangle snapshot). The BVH BUILD is the dominant CPU cost on big scenes (572-900ms for
    // 200K+ tris); KickBake used to call BakeWorldTriangles which rebuilt the BVH on EVERY bake — both
    // the coarse warm-up AND the full-res refine of the same cascade paid it twice. Caching the prepared
    // field per cascade (keyed by the geometry stamp it was built under) lets the coarse bake and the
    // refine share ONE build. Invalidated wholesale on a geometry-stamp change (same as cascadeBaked).
    readonly MeshSdfBaker.PreparedField[] preparedField = new MeshSdfBaker.PreparedField[CascadeCount];
    readonly int[] preparedStamp = new int[CascadeCount];   // geometryStamp the prepared field was built under
    readonly bool[] preparedValid = new bool[CascadeCount];

    // PHASE 1 (GPU JFA): the jump-flood SDF builder + a one-shot correctness self-test gate. The
    // builder is created lazily on first use (BALLISTIC_LUMEN_JFA / BALLISTIC_JFA_SELFTEST). The CPU
    // bake stays the default until the JFA path is proven; the self-test compares the two voxel-by-voxel.
    GLSdfJfaBuilder jfaBuilder;
    static readonly bool JfaSelfTest = Environment.GetEnvironmentVariable("BALLISTIC_JFA_SELFTEST") == "1";
    bool jfaSelfTestDone;

    // Voxel-lighting inject program (GlobalRadianceInject_Comp) + its cached uniform locations.
    int injectProgram;
    int liCascade, liCascadeMin, liCascadeCell, liRes, liSkyExposure, liFeedback, liBounceScale;
    // Multi-bounce gain (BALLISTIC_LUMEN_BOUNCE, default 2). >1 strengthens the indirect inter-surface
    // bounce in the voxel cache so enclosed/shadowed areas fill with colored multi-bounce light. The
    // stored cache is a geometric series in bounceAlbedo*gain, so it accelerates non-linearly: on
    // SunTemple (isolated bounce, fixed EV) gain 1 -> (9,6,3), gain 2 -> (12,7,4) [visibly richer fill +
    // colour, contrast kept], gain 3 -> (48,39,30) [washes out, runaway]. 2 is the sweet spot for
    // visible Lumen-class multi-bounce without blowing out — verified on SunTemple + both Bistro scenes.
    static readonly float BounceScale = ParseBounce();
    static float ParseBounce() {
        string s = Environment.GetEnvironmentVariable("BALLISTIC_LUMEN_BOUNCE");
        return float.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out float v)
            ? Math.Clamp(v, 0f, 8f) : 2f;
    }
    int liCascadeCountSun, liSunDir, liSunColor, liAlbedo, liCascadeBias;
    int liPointCount;
    readonly int[] liPointPos = new int[8];
    readonly int[] liPointColor = new int[8];
    readonly int[] liPointRange = new int[8];
    int liSpotCount;
    readonly int[] liSpotPos = new int[4];
    readonly int[] liSpotDir = new int[4];
    readonly int[] liSpotColor = new int[4];
    readonly int[] liSpotRange = new int[4];
    readonly int[] liSpotCosInner = new int[4];
    readonly int[] liSpotCosOuter = new int[4];
    readonly int[] liCascadeMatrices = new int[4];
    readonly int[] liGdfMin = new int[CascadeCount];
    readonly int[] liGdfCell = new int[CascadeCount];

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

            albedoTex[c] = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture3D, albedoTex[c]);
            GL.TexStorage3D(TextureTarget3d.Texture3D, 1, SizedInternalFormat.Rgba8, Resolution, Resolution, Resolution);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
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
        liBounceScale = GL.GetUniformLocation(injectProgram, "BounceScale");
        liCascadeCountSun = GL.GetUniformLocation(injectProgram, "CascadeCountSun");
        liSunDir = GL.GetUniformLocation(injectProgram, "SunDirectionWorld");
        liSunColor = GL.GetUniformLocation(injectProgram, "SunColor");
        liAlbedo = GL.GetUniformLocation(injectProgram, "Albedo");
        liCascadeBias = GL.GetUniformLocation(injectProgram, "CascadeBias");
        liPointCount = GL.GetUniformLocation(injectProgram, "PointCount");
        for (int i = 0; i < 8; i++) {
            liPointPos[i] = GL.GetUniformLocation(injectProgram, $"PointPos[{i}]");
            liPointColor[i] = GL.GetUniformLocation(injectProgram, $"PointColor[{i}]");
            liPointRange[i] = GL.GetUniformLocation(injectProgram, $"PointRange[{i}]");
        }
        liSpotCount = GL.GetUniformLocation(injectProgram, "SpotCount");
        for (int i = 0; i < 4; i++) {
            liSpotPos[i] = GL.GetUniformLocation(injectProgram, $"SpotPos[{i}]");
            liSpotDir[i] = GL.GetUniformLocation(injectProgram, $"SpotDir[{i}]");
            liSpotColor[i] = GL.GetUniformLocation(injectProgram, $"SpotColor[{i}]");
            liSpotRange[i] = GL.GetUniformLocation(injectProgram, $"SpotRange[{i}]");
            liSpotCosInner[i] = GL.GetUniformLocation(injectProgram, $"SpotCosInner[{i}]");
            liSpotCosOuter[i] = GL.GetUniformLocation(injectProgram, $"SpotCosOuter[{i}]");
        }
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

    // Called each frame on the GL thread. Kicks ONE background bake at a time (uploading the previous
    // result if ready), in a warm-up-priority order: (1) get a COARSE field into every cascade with
    // none, so the whole clipmap is live within a few frames; (2a) re-place any scrolled cascade coarse
    // (instant tracking under camera motion); (2b) refine the finest still-coarse cascade to full res.
    // So Lumen contributes within a frame or two (soft), then sharpens — instead of stalling seconds on
    // cascade 0's full-res bake.
    public void Update(Vector3 cameraPos, IReadOnlyList<IStaticMeshRenderer> opaque) {
        // Finish a completed background bake: upload it (GL thread).
        if (bakeTask is { IsCompleted: true }) {
            if (bakeTask.IsFaulted && Environment.GetEnvironmentVariable("BALLISTIC_LUMEN_DIAG") == "1")
                Console.WriteLine($"[GlobalSdf] bake FAULTED cascade {bakeCascade}: {bakeTask.Exception?.GetBaseException().Message}");
            BakeResult r = bakeTask.Status == TaskStatus.RanToCompletion ? bakeTask.Result : null;
            if (r is { Sdf: not null }) {
                // The field was baked at r.Res (coarse or full); upsample to the full texture grid so
                // the march/placement never change. A full-res bake upsamples 1:1 (a copy).
                UploadCascade(r.Cascade, r.Sdf, r.Albedo, r.Min, r.Cell);
                cascadeBaked[r.Cascade] = true;
                cascadeRes[r.Cascade] = r.Res;
                if (r.Cascade == 0) Available = true;
                // PHASE 0: cache the built BVH so this cascade's full-res refine reuses it (the build is
                // the dominant cost). Keyed by the stamp it was built under; PASS 2a scroll re-bakes a
                // moved cascade with a fresh snapshot, so the prepared field is only reused in-place.
                if (r.Prepared != null) {
                    preparedField[r.Cascade] = r.Prepared;
                    preparedStamp[r.Cascade] = geometryStamp;
                    preparedValid[r.Cascade] = true;
                }

                // PHASE 1 self-test: when cascade 0 has a full-res prepared field, build the JFA field
                // over the SAME box/res and compare voxel-by-voxel against the CPU bake. One-shot.
                if (JfaSelfTest && !jfaSelfTestDone && r.Cascade == 0 && r.Prepared != null) {
                    jfaSelfTestDone = true;
                    jfaBuilder ??= new GLSdfJfaBuilder(Resolution);
                    Vector3 boxMax = cascadeMin[0] + new Vector3(cascadeCell[0] * Resolution);
                    jfaBuilder.SelfTest(r.Prepared, cascadeMin[0], boxMax, new Vector3i(Resolution));
                }
            }
            bakeTask = null;
            bakeCascade = -1;
        }
        if (bakeTask is not null)
            return; // a bake is in flight — one at a time keeps the BVH/grid cost bounded per frame

        // Geometry change => invalidate every cascade (re-bake from scratch, coarse-first again).
        int stamp = GeometryStamp(opaque);
        if (!stampValid || stamp != geometryStamp) {
            geometryStamp = stamp;
            stampValid = true;
            for (int c = 0; c < CascadeCount; c++) {
                cascadeBaked[c] = false; cascadeRes[c] = 0;
                preparedValid[c] = false; preparedField[c] = null; // stale BVH — geometry moved
            }
        }

        // PASS 1 (warm-up priority): get a COARSE field into EVERY cascade that has none, finest-first.
        // 32^3 bakes in a fraction of a second, so within ~4 frames the whole clipmap is live (Lumen
        // contributes immediately) instead of waiting seconds for even cascade 0's full-res bake.
        for (int c = 0; c < CascadeCount; c++) {
            if (cascadeRes[c] != 0)
                continue;
            float extent = ExtentOf(c);
            float cell = extent / Resolution;
            Vector3 snapped = Snap(cameraPos - new Vector3(extent * 0.5f), cell);
            KickBake(c, snapped, cell, opaque, CoarseResolution);
            return;
        }

        // PASS 2a: any cascade the camera SCROLLED past half a voxel re-places COARSE first (instant), so
        // a moving camera never blocks on a full bake — the field tracks the view immediately, then PASS
        // 2b sharpens it. Scroll handling takes priority over refinement (cheap coarse re-place beats a
        // ~1s full bake when the view is actually moving). Finest-first.
        for (int c = 0; c < CascadeCount; c++) {
            float extent = ExtentOf(c);
            float cell = extent / Resolution;
            Vector3 snapped = Snap(cameraPos - new Vector3(extent * 0.5f), cell);
            if ((snapped - cascadeMin[c]).Length > cell * 0.5f) {
                cascadeRes[c] = 0;
                KickBake(c, snapped, cell, opaque, CoarseResolution);
                return;
            }
        }

        // PASS 2b: nothing scrolled — refine the finest still-coarse cascade to full res, in place.
        for (int c = 0; c < CascadeCount; c++) {
            if (cascadeRes[c] < Resolution) {
                KickBake(c, cascadeMin[c], cascadeCell[c], opaque, Resolution);
                return;
            }
        }
    }

    static Vector3 Snap(Vector3 v, float cell) => new(
        MathF.Floor(v.X / cell) * cell, MathF.Floor(v.Y / cell) * cell, MathF.Floor(v.Z / cell) * cell);

    // Snapshot the opaque triangles overlapping this cascade's box (WORLD space) on the GL thread, then
    // bake the field on a background task (BVH + parallel grid). The snapshot is essential — the bake
    // can't touch engine state off-thread.
    void KickBake(int cascade, Vector3 min, float cell, IReadOnlyList<IStaticMeshRenderer> opaque,
        int detailRes) {
        float extent = cell * Resolution;
        Vector3 max = min + new Vector3(extent);

        // Pad the cull box by a couple cells so triangles just outside still seed the boundary field.
        Vector3 cullMin = min - new Vector3(cell * 2f);
        Vector3 cullMax = max + new Vector3(cell * 2f);

        // SUB-CELL TRIANGLE CULL (bake-cost win, geometrically safe): a triangle whose world AABB is
        // smaller than ~half a voxel can't be RESOLVED by this grid — it falls inside one cell, where
        // the field is already a single coarse distance. Skipping it removes it from the BVH (whose
        // BUILD is the dominant cost on big scenes — 900ms for 342K tris) WITHOUT making holes: a wall
        // is many triangles, so its big spanning tris remain; only tiny clutter/trim (which the coarse
        // field flattens anyway) drops. The threshold SHRINKS with the cell, so the full-res 96^3 bake
        // (0.125m cells) keeps nearly everything while a coarse 96m cascade (3m cells) sheds the long
        // tail of detail tris it could never represent. cellSq compared against the AABB diagonal^2.
        float minTriExtent = cell * 0.5f;
        float minTriExtentSq = minTriExtent * minTriExtent;

        var worldVerts = new List<Vector3>(4096);
        var triAlbedo = new List<Vector3>(1400); // one linear-RGB albedo per triangle (Lumen albedo field)
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
                // The submesh's material base colour (linear RGB) — the surface albedo this voxel bounces.
                Vector3 alb = mat != null ? mat.BaseColorFactor.Xyz : new Vector3(0.5f);
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
                    // Sub-cell cull: skip tris smaller than ~half a voxel (unresolvable; see note above).
                    if ((tmax - tmin).LengthSquared < minTriExtentSq)
                        continue;
                    worldVerts.Add(a); worldVerts.Add(b); worldVerts.Add(cc);
                    triAlbedo.Add(alb);
                }
            }
        }

        if (worldVerts.Count == 0) {
            // No geometry in this cascade — mark baked (the cleared far-distance field is correct: empty).
            // An empty cascade needs no refinement, so mark it FULLY resolved (skip the PASS 2 re-bake).
            cascadeBaked[cascade] = true;
            cascadeRes[cascade] = Resolution;
            cascadeMin[cascade] = min;
            cascadeCell[cascade] = cell;
            if (cascade == 0) Available = true;
            return;
        }

        cascadeMin[cascade] = min;
        cascadeCell[cascade] = cell;
        // Bake at the requested DETAIL resolution (coarse for warm-up, full to refine). The box stays
        // the cascade's full world extent; only the voxel grid is coarsened, so the upsample on upload
        // maps it 1:1 back into the full-res texture (placement unchanged).
        int dr = Math.Clamp(detailRes, 8, Resolution);
        var res = new Vector3i(dr);
        // Coarse warm-up bakes use a cheaper 3-ray sign (the 7-ray vote is the per-voxel cost when large
        // coarse cells put most voxels inside the sign band); the full-res refine uses the robust 7 rays.
        int signRays = dr < Resolution ? 3 : 7;
        bakeCascade = cascade;
        bool diag = Environment.GetEnvironmentVariable("BALLISTIC_LUMEN_DIAG") == "1";
        int triCount = worldVerts.Count / 3;

        // PHASE 0 (BVH reuse): reuse this cascade's prepared field (built BVH) when it was built under the
        // CURRENT geometry stamp AND covers a box overlapping this bake (same snapshot). The coarse warm-up
        // built it; the full-res refine of the SAME cascade reuses it instead of rebuilding the BVH. A
        // cascade that scrolled (PASS 2a, new box/triangles) rebuilds — its prepared field no longer matches.
        bool reuse = preparedValid[cascade] && preparedStamp[cascade] == geometryStamp
                     && (cascadeMin[cascade] - min).LengthSquared < 1e-6f;
        MeshSdfBaker.PreparedField reused = reuse ? preparedField[cascade] : null;

        bakeTask = Task.Run(() => {
            var sw = diag ? System.Diagnostics.Stopwatch.StartNew() : null;
            // Build the BVH once (or reuse the cached one), then bake the grid at this detail resolution.
            MeshSdfBaker.PreparedField prep = reused ?? MeshSdfBaker.Prepare(worldVerts, triAlbedo);
            MeshSdf sdf = null;
            float[] alb = null;
            if (prep != null)
                sdf = MeshSdfBaker.BakePrepared(prep, min, max, res, out alb, signRays);
            if (sw != null)
                Console.WriteLine($"[GDF bake] cascade {cascade} res {dr}^3 sign{signRays}, {triCount} tris" +
                                  $"{(reused != null ? " (BVH reused)" : "")} -> {sw.ElapsedMilliseconds} ms");
            return new BakeResult {
                Cascade = cascade, Sdf = sdf, Albedo = alb, Min = min, Cell = cell,
                Res = dr, SrcRes = sdf?.Res ?? res, Prepared = prep
            };
        });
    }

    static Vector3 Transform(Vector3 v, Matrix4 m) => (new Vector4(v, 1f) * m).Xyz;

    void UploadCascade(int cascade, MeshSdf sdf, float[] albedo, Vector3 min, float cell) {
        cascadeMin[cascade] = min;
        cascadeCell[cascade] = cell;

        // The GPU texture is always Resolution^3. A full-res bake uploads its grid directly (the fast,
        // byte-identical-to-before path). A COARSE warm-up bake (e.g. 32^3) is trilinearly UPSAMPLED to
        // Resolution^3 on the CPU first — sampling a tiny field 884K times is microseconds, far below
        // the bake itself, and keeps the march/clipmap placement unchanged (they only know the texture
        // res). The next full-res bake of this cascade overwrites it sharp.
        bool fullRes = sdf.Res.X == Resolution && sdf.Res.Y == Resolution && sdf.Res.Z == Resolution;
        float[] dist = fullRes ? sdf.Distances : UpsampleDistance(sdf);
        float[] alb = albedo == null ? null : (fullRes ? albedo : UpsampleAlbedo(sdf, albedo));

        GL.BindTexture(TextureTarget.Texture3D, textures[cascade]);
        // x-fastest source matches MeshSdf.Index = x + Res.X*(y + Res.Y*z); R16F narrows from float.
        GL.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, 0,
            Resolution, Resolution, Resolution, PixelFormat.Red, PixelType.Float, dist);
        GL.BindTexture(TextureTarget.Texture3D, 0);

        if (alb != null) {
            GL.BindTexture(TextureTarget.Texture3D, albedoTex[cascade]);
            GL.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, 0,
                Resolution, Resolution, Resolution, PixelFormat.Rgb, PixelType.Float, alb);
            GL.BindTexture(TextureTarget.Texture3D, 0);
        }
    }

    // Trilinearly expand a coarse signed-distance field to the full Resolution^3 grid (warm-up only).
    // MeshSdf.Sample reads in the field's local space at cell centers, so we sample the full grid's
    // cell centers in the SAME box — the upsample is geometrically exact for the field it has.
    float[] UpsampleDistance(MeshSdf sdf) {
        var dst = new float[Resolution * Resolution * Resolution];
        Vector3 ext = sdf.BoundsMax - sdf.BoundsMin;
        Vector3 cellFull = ext / Resolution;
        System.Threading.Tasks.Parallel.For(0, Resolution, z => {
            for (int y = 0; y < Resolution; y++)
                for (int x = 0; x < Resolution; x++) {
                    Vector3 p = sdf.BoundsMin + new Vector3(
                        (x + 0.5f) * cellFull.X, (y + 0.5f) * cellFull.Y, (z + 0.5f) * cellFull.Z);
                    dst[x + Resolution * (y + Resolution * z)] = sdf.Sample(p);
                }
        });
        return dst;
    }

    // Nearest-cell expand of the coarse per-voxel albedo to the full grid (RGB, x-fastest, 3 floats/
    // voxel). Albedo is piecewise-flat per surface, so nearest is fine (and avoids smearing colours
    // across material boundaries that trilinear would do).
    float[] UpsampleAlbedo(MeshSdf sdf, float[] srcRgb) {
        var dst = new float[Resolution * Resolution * Resolution * 3];
        Vector3i s = sdf.Res;
        System.Threading.Tasks.Parallel.For(0, Resolution, z => {
            int sz = Math.Min(s.Z - 1, (int)((z + 0.5f) / Resolution * s.Z));
            for (int y = 0; y < Resolution; y++) {
                int sy = Math.Min(s.Y - 1, (int)((y + 0.5f) / Resolution * s.Y));
                for (int x = 0; x < Resolution; x++) {
                    int sx = Math.Min(s.X - 1, (int)((x + 0.5f) / Resolution * s.X));
                    int si = (sx + s.X * (sy + s.Y * sz)) * 3;
                    int di = (x + Resolution * (y + Resolution * z)) * 3;
                    dst[di] = srcRgb[si]; dst[di + 1] = srcRgb[si + 1]; dst[di + 2] = srcRgb[si + 2];
                }
            }
        });
        return dst;
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
    // GI REWORK Phase 1 (2026-06-14): light ALL baked cascades EVERY frame (was 1-of-4 round-robin).
    // The round-robin meant a cascade was only re-lit every 4 frames and, combined with the sticky EMA,
    // took 50-160 frames to converge — so rooms stayed perpetually half-lit (BistroInterior isolated
    // bounce was ~empty). Real Lumen lights the whole surface cache each frame; convergence comes from
    // the temporal filter AFTER the gather, not from a slow cache fill. On a high-end GPU the 4 dispatches
    // (4x Resolution^3/64 invocations) are a few ms — acceptable per the GPU-heavy budget.
    // Called by GLSdfGiPass.Render (it has the sun/shadow/sky params), AFTER Update has placed/baked the
    // cascades. Ping-pong: read last frame's radiance (samplers), write this frame's, swap per cascade.
    public void InjectRadiance(int irradianceCubemap, int shadowMapArray, Matrix4[] sunCascades,
        Vector4 sunBias, int sunCascadeCount, Vector3 sunDir, Vector3 sunColor, Vector3 albedo,
        float skyExposure, float feedback,
        int pointCount = 0, Vector3[] pointPos = null, Vector3[] pointColor = null, float[] pointRange = null,
        int spotCount = 0, Vector3[] spotPos = null, Vector3[] spotDir = null, Vector3[] spotColor = null,
        float[] spotRange = null, float[] spotCosInner = null, float[] spotCosOuter = null) {
        if (!Available || injectProgram == 0)
            return;

        GL.UseProgram(injectProgram);

        // ---- Cascade-INDEPENDENT bindings + uniforms: set once, shared by every cascade dispatch ----
        BindSampler(3, TextureTarget.TextureCubeMap, irradianceCubemap);
        BindSampler(5, TextureTarget.Texture2DArray, shadowMapArray);
        GL.Uniform1(GL.GetUniformLocation(injectProgram, "IrradianceMap"), 3);
        GL.Uniform1(GL.GetUniformLocation(injectProgram, "ShadowMap"), 5);
        GL.Uniform1(GL.GetUniformLocation(injectProgram, "AlbedoField"), 14);
        GL.Uniform1(GL.GetUniformLocation(injectProgram, "DistanceField"), 0);
        // The whole GDF: distances on units 6..9 (for the bounce sphere-trace). The per-cascade LAST-frame
        // radiance samplers (10..13) are re-bound INSIDE the loop so a cascade lit earlier this frame is
        // visible to the next cascade's bounce — but the placement uniforms are cascade-independent.
        for (int k = 0; k < CascadeCount; k++) {
            BindSampler(6 + k, TextureTarget.Texture3D, textures[k]);
            GL.Uniform1(GL.GetUniformLocation(injectProgram, $"GdfDistance[{k}]"), 6 + k);
            GL.Uniform1(GL.GetUniformLocation(injectProgram, $"GdfRadiance[{k}]"), 10 + k);
            GL.Uniform3(liGdfMin[k], cascadeMin[k].X, cascadeMin[k].Y, cascadeMin[k].Z);
            GL.Uniform1(liGdfCell[k], cascadeCell[k]);
        }
        GL.Uniform1(liRes, Resolution);
        GL.Uniform1(liSkyExposure, skyExposure);
        GL.Uniform1(liFeedback, feedback);
        GL.Uniform1(liBounceScale, BounceScale);
        int sc = Math.Min(sunCascadeCount, 4);
        GL.Uniform1(liCascadeCountSun, sc);
        for (int i = 0; i < sc; i++)
            GL.UniformMatrix4(liCascadeMatrices[i], false, ref sunCascades[i]);
        GL.Uniform4(liCascadeBias, sunBias);
        GL.Uniform3(liSunDir, sunDir);
        GL.Uniform3(liSunColor, sunColor);
        GL.Uniform3(liAlbedo, albedo);

        // Punctual lights into the surface cache (so point-lit interiors get Lumen bounce).
        int pc = pointPos == null ? 0 : Math.Min(pointCount, Math.Min(8, pointPos.Length));
        GL.Uniform1(liPointCount, pc);
        for (int i = 0; i < pc; i++) {
            GL.Uniform3(liPointPos[i], pointPos[i].X, pointPos[i].Y, pointPos[i].Z);
            GL.Uniform3(liPointColor[i], pointColor[i].X, pointColor[i].Y, pointColor[i].Z);
            GL.Uniform1(liPointRange[i], pointRange[i]);
        }
        // Spot lights into the surface cache.
        int spc = spotPos == null ? 0 : Math.Min(spotCount, Math.Min(4, spotPos.Length));
        GL.Uniform1(liSpotCount, spc);
        for (int i = 0; i < spc; i++) {
            GL.Uniform3(liSpotPos[i], spotPos[i].X, spotPos[i].Y, spotPos[i].Z);
            GL.Uniform3(liSpotDir[i], spotDir[i].X, spotDir[i].Y, spotDir[i].Z);
            GL.Uniform3(liSpotColor[i], spotColor[i].X, spotColor[i].Y, spotColor[i].Z);
            GL.Uniform1(liSpotRange[i], spotRange[i]);
            GL.Uniform1(liSpotCosInner[i], spotCosInner[i]);
            GL.Uniform1(liSpotCosOuter[i], spotCosOuter[i]);
        }

        int g = (Resolution + 3) / 4;

        // ---- Light EVERY baked cascade this frame ----
        for (int c = 0; c < CascadeCount; c++) {
            if (!cascadeBaked[c])
                continue;
            // Write target: this cascade's WRITE radiance texture as image 1; its distance + albedo fields.
            GL.BindImageTexture(1, RadianceWrite(c), 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
            BindSampler(0, TextureTarget.Texture3D, textures[c]);
            BindSampler(14, TextureTarget.Texture3D, albedoTex[c]);
            // Re-bind the per-cascade LAST-frame radiance read samplers (10..13). Within this loop a cascade
            // already swapped becomes the freshly-written one, so later cascades' bounce sees this frame's
            // light — extra in-frame propagation, harmless to convergence (it's still EMA-blended).
            for (int k = 0; k < CascadeCount; k++)
                BindSampler(10 + k, TextureTarget.Texture3D, RadianceRead(k));
            GL.Uniform1(liCascade, c);
            GL.Uniform3(liCascadeMin, cascadeMin[c].X, cascadeMin[c].Y, cascadeMin[c].Z);
            GL.Uniform1(liCascadeCell, cascadeCell[c]);

            GL.DispatchCompute(g, g, g);
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);
            SwapRadiance(c); // only the cascade we just wrote flips (per-cascade ping-pong)
        }
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

    // Diagnostic (BALLISTIC_LUMEN_DIAG): per-cascade baked DETAIL resolution (0 = none, CoarseResolution
    // = soft warm-up field up, Resolution = fully refined). Lets the renderer log the warm-up progress.
    public int CascadeBakedRes(int c) => cascadeRes[c];

    public void Dispose() {
        for (int c = 0; c < CascadeCount; c++) {
            if (textures[c] != 0) GL.DeleteTexture(textures[c]);
            if (radianceA[c] != 0) GL.DeleteTexture(radianceA[c]);
            if (radianceB[c] != 0) GL.DeleteTexture(radianceB[c]);
            if (albedoTex[c] != 0) GL.DeleteTexture(albedoTex[c]);
        }
        if (injectProgram != 0) GL.DeleteProgram(injectProgram);
        jfaBuilder?.Dispose();
        Available = false;
    }
}
