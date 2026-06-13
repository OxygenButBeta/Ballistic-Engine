using System;
using System.Collections.Generic;
using BallisticEngine;
using BallisticEngine.GI;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace BallisticEngine.OpenGL.GI;

// DESIGN.md component 4 (P6.3) — the SDF World-Space GI pass. Owns the whole subsystem:
//   * a GLSdfAtlas (one R16F 3D texture packing each distinct mesh's baked MeshSdf),
//   * a GLSdfScene (the per-instance worldToLocal + slot-table SSBOs the march reads),
//   * the compiled SdfTrace_Comp program, and
//   * a half-res RGBA16F output the consumer (SSGI) reads SAME-FRAME.
//
// Default-OFF: BALLISTIC_SDFGI=1 gates the entire thing. With the flag off (or no compute) the
// pass reports Available=false and is never dispatched — the renderer stays byte-identical to the
// committed baseline. The integrator (GLHDRenderer) must check Available before calling Render.
//
// Baking is bounded and one-time: a mesh is baked at most once (keyed by Mesh.InstanceId) at a
// small fixed resolution, packed into the atlas, and its slot index cached. EnsureBaked runs per
// frame but only does work the first time it sees a new mesh; the per-frame cost after warm-up is
// just rebuilding the (cheap) instance SSBO from the current transforms.
//
// Layering: OpenGL/ may use GL + Engine/Abstraction types. MeshSdf/MeshSdfBaker/MeshData live in
// Abstraction (BallisticEngine.GI / BallisticEngine). No asset I/O here — we bake straight from the
// retained CPU geometry (Mesh.Vertices/Indices/...), so no .bmesh artifact path is needed.
public sealed class GLSdfGiPass : IDisposable {
    // Bound resolution for the per-mesh SDF bake (longest-axis cell count). Small on purpose — the
    // off-screen indirect gather is low-frequency, and a coarse field keeps both the bake time and
    // the atlas footprint bounded. 40^3 worst case ~= 0.13 MB R16F per mesh.
    const int BakeResolution = 40;

    // Hard cap on distinct meshes baked into the atlas. Overflow logs once and is skipped (those
    // renderers simply don't contribute off-screen GI; never a crash, never silent truncation).
    const int MaxDistinctMeshes = 64;

    // Image unit / sampler units the compute uses (match SdfTrace_Comp.glsl layout(binding=...)):
    //   image 0 = OutGi, sampler 1 = Depth, 2 = Normal, 3 = Irradiance cube, 4 = SDF atlas.
    const int OutGiImageUnit = 0;
    const int DepthUnit = 1;
    const int NormalUnit = 2;
    const int IrradianceUnit = 3;
    const int AtlasUnit = 4;
    const int ShadowUnit = 5;   // sampler2DArrayShadow — direct-sun visibility at SDF hits

    // Neutral diffuse albedo for off-screen SDF hits (no per-hit material in v1). Mid-grey.
    const float HitAlbedo = 0.5f;

    static readonly string[] CascadeMatrixNames =
        { "CascadeMatrices[0]", "CascadeMatrices[1]", "CascadeMatrices[2]", "CascadeMatrices[3]" };

    // Master strength of the additive off-screen bounce. Tuned DOWN from 1.0 — at full strength the
    // single-bounce fill washed already-lit surfaces flat; ~0.4 fills the shadowed recesses (the
    // point) while keeping contrast. Overridable via BALLISTIC_SDFGI_INTENSITY for A/B tuning.
    readonly float sdfGiIntensity = ParseIntensity();
    static float ParseIntensity() {
        string s = Environment.GetEnvironmentVariable("BALLISTIC_SDFGI_INTENSITY");
        return float.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out float v)
            ? Math.Clamp(v, 0f, 4f) : 0.4f;
    }

    public bool Available { get; private set; }

    readonly GLSdfAtlas atlas;
    readonly GLSdfScene scene;
    readonly AtlasAdapter atlasAdapter;

    int program;

    // Full-res depth-aware upsample + additive composite (SdfGi_Combine.glsl). Mirrors SSR_Combine
    // but ADDS (GI only lifts, never darkens) and returns the modified litColor, so the pass plugs
    // in like GLSSGIPass — no Frag.glsl change needed.
    readonly StandardShader combineShader;

    // BALLISTIC_SDFGI_DEBUG=1: the composite outputs ONLY the gathered GI so an enclosed-scene
    // screenshot shows the raw off-screen bounce.
    readonly bool debugView;

    // BALLISTIC_SDFGI_DIAG=1: the gather outputs the per-pixel HIT FRACTION (grayscale) instead of
    // radiance — white = every ray hit SDF geometry, black = every ray escaped to sky. Forces the
    // debug composite so the raw value is visible. Disambiguates "rays miss" (granularity) from
    // "radiance too dim" (needs surface cache). Implies debugView.
    readonly bool diagMode;

    // Cached uniform locations (looked up once after the link).
    int locInvProjection, locInvView, locInstanceCount, locHalfSize, locSkyExposure, locFrameIndex, locDiagMode;
    int locCascadeBias, locCascadeCount, locSunDir, locSunColor, locHitAlbedo;
    readonly int[] locCascadeMatrices = new int[4];

    // Half-res RGBA16F gather output (pass-owned — small, consumed same frame; not pooled because
    // we bind it as an image, so a stable id is required). The full-res composite goes to a pooled
    // transient target (acquired per frame, released wholesale in EndFrame).
    readonly GLRenderTexture output = new();
    int outW, outH;

    // Mesh.InstanceId -> atlas slot index. The bake/upload done-set: a mesh in this map is already
    // packed into the atlas and never re-baked. -1 marks a mesh that failed to fit (skip silently
    // on later frames; the overflow was already logged once).
    readonly Dictionary<Guid, int> meshSlots = new();
    bool overflowLogged;

    // Scratch instance list reused across frames (allocation-light per the GLSdfScene contract).
    readonly List<(Matrix4 world, int slot)> instances = new();

    int frameIndex;

    public GLSdfGiPass() {
        // Honour the default-OFF gate FIRST: if the flag is not 1 we build nothing GPU-side and
        // report unavailable, so the pass can be constructed unconditionally by the renderer.
        bool enabled = Environment.GetEnvironmentVariable("BALLISTIC_SDFGI") == "1";
        if (!enabled) {
            Available = false;
            return;
        }

        diagMode = Environment.GetEnvironmentVariable("BALLISTIC_SDFGI_DIAG") == "1";
        debugView = diagMode || Environment.GetEnvironmentVariable("BALLISTIC_SDFGI_DEBUG") == "1";

        atlas = new GLSdfAtlas(GLSdfAtlas.DefaultSize);
        scene = new GLSdfScene();
        atlasAdapter = new AtlasAdapter(atlas);

        program = CompileCompute(EmbeddedShaderSource.Read("SdfTrace_Comp.glsl"));
        if (program == 0) {
            // Compile/link failed (logged by CompileCompute). Auto-disable; clean up the GPU
            // resources we already created so an unavailable pass owns nothing live.
            atlas.Dispose();
            scene.Dispose();
            Available = false;
            return;
        }

        // Full-res additive composite (depth-aware upsample). FSQ vertex stage shared with the
        // other post passes; the fragment does the upsample + scene + gi*intensity + NaN scrub.
        combineShader = GraphicAPI.CreateStandardShader(
            EmbeddedShaderSource.Read("FSQ_Vert.glsl"),
            EmbeddedShaderSource.Read("SdfGi_Combine.glsl"));

        CacheUniformLocations();
        Available = true;
    }

    void CacheUniformLocations() {
        locInvProjection = GL.GetUniformLocation(program, "InvProjection");
        locInvView = GL.GetUniformLocation(program, "InvView");
        locInstanceCount = GL.GetUniformLocation(program, "InstanceCount");
        locHalfSize = GL.GetUniformLocation(program, "HalfSize");
        locSkyExposure = GL.GetUniformLocation(program, "SkyExposure");
        locFrameIndex = GL.GetUniformLocation(program, "FrameIndex");
        locDiagMode = GL.GetUniformLocation(program, "DiagMode");
        locCascadeBias = GL.GetUniformLocation(program, "CascadeBias");
        locCascadeCount = GL.GetUniformLocation(program, "CascadeCount");
        locSunDir = GL.GetUniformLocation(program, "SunDirectionWorld");
        locSunColor = GL.GetUniformLocation(program, "SunColor");
        locHitAlbedo = GL.GetUniformLocation(program, "HitAlbedo");
        for (var i = 0; i < 4; i++)
            locCascadeMatrices[i] = GL.GetUniformLocation(program, CascadeMatrixNames[i]);
    }

    // Bakes + uploads the SDF for every distinct mesh in the opaque set (once each, keyed by
    // Mesh.InstanceId), then rebuilds the GLSdfScene instance SSBO from the current per-renderer
    // world transforms. Cheap after warm-up: no re-bake, just the instance-list rebuild + upload.
    //
    // Bake happens on the calling (GL) thread here — TryAdd issues GL calls so the upload must be on
    // the GL thread, and the bake is bounded (BakeResolution^3, BVH-accelerated, one-time per mesh).
    // For very large meshes a future pass can move the CPU bake off-thread and only TryAdd here.
    public void EnsureBaked(IReadOnlyList<IStaticMeshRenderer> opaque) {
        if (!Available || opaque == null)
            return;

        // ---- Bake + pack any not-yet-seen distinct meshes ----
        for (var i = 0; i < opaque.Count; i++) {
            IStaticMeshRenderer r = opaque[i];
            if (r is not { IsRenderable: true })
                continue;
            Mesh mesh = r.SharedMesh;
            if (mesh == null || mesh.Vertices is not { Length: > 0 } || mesh.Indices is not { Length: > 0 })
                continue;

            Guid key = mesh.InstanceId;
            if (meshSlots.ContainsKey(key))
                continue; // already baked (or already marked failed with -1)

            if (meshSlots.Count >= MaxDistinctMeshes) {
                if (!overflowLogged) {
                    Debugging.Log($"[GLSdfGiPass] distinct-mesh cap {MaxDistinctMeshes} reached; " +
                                  "remaining meshes contribute no off-screen GI (raise the cap or " +
                                  "use per-submesh fields).");
                    overflowLogged = true;
                }
                meshSlots[key] = -1; // remember the skip so we don't re-test it every frame
                continue;
            }

            // Wrap the retained CPU geometry as a MeshData view and bake at the bounded resolution.
            var data = new MeshData(mesh.Vertices, mesh.Indices, mesh.UVs, mesh.Normals, mesh.Tangents);
            MeshSdf sdf = MeshSdfBaker.Bake(data, new MeshSdfBaker.Settings(BakeResolution));
            if (sdf == null) {
                meshSlots[key] = -1;
                continue;
            }

            if (atlas.TryAdd(sdf, out int slot)) {
                meshSlots[key] = slot;
            } else {
                // Atlas full (or the field didn't fit) — TryAdd already logged. Mark as skipped.
                meshSlots[key] = -1;
            }
        }

        // ---- Rebuild the instance list from the current transforms ----
        instances.Clear();
        for (var i = 0; i < opaque.Count; i++) {
            IStaticMeshRenderer r = opaque[i];
            if (r is not { IsRenderable: true, IsActive: true })
                continue;
            Mesh mesh = r.SharedMesh;
            if (mesh == null)
                continue;
            if (!meshSlots.TryGetValue(mesh.InstanceId, out int slot) || slot < 0)
                continue; // not baked or didn't fit — no off-screen GI for this renderer
            Transform t = r.Transform;
            if (t == null)
                continue;
            instances.Add((t.WorldMatrix, slot));
        }

        scene.Build(instances, atlasAdapter);
    }

    // Runs the SDF-GI as a POST pass (mirroring GLSSGIPass): dispatch the half-res compute gather,
    // then depth-aware upsample + ADDITIVELY composite the off-screen bounce onto the full-res lit
    // colour, returning the NEW litColor texture id. Purely additive — GI only ever lifts a surface,
    // never darkens below the no-GI baseline (the hard-won lesson). With the flag off (Available ==
    // false) this is never reached and the renderer stays byte-identical to the baseline.
    //
    // Returns colorTexture UNCHANGED when there's nothing to trace (no baked instances) — so the
    // frame is identical to no-SDF-GI in that case. BALLISTIC_SDFGI_DEBUG=1 makes the composite
    // output ONLY the gathered GI so an enclosed-scene screenshot shows the raw off-screen bounce.
    //
    // colorTexture is the full-res lit scene (the litColor the integrator threads through the post
    // chain). depthTex / normalTex come from the G-buffer (target.DepthTextureId /
    // target.NormalTextureId); irradianceCubemap is the baked IBL diffuse irradiance (hit/miss
    // radiance source). width/height are the FULL-res viewport. view/projection are this frame's
    // camera matrices (the shader uses their inverses for the same reconstruction SSGI/SSR use).
    public int Render(int colorTexture, int depthTex, int normalTex, int irradianceCubemap,
        int shadowMapArray, Matrix4[] cascadeMatrices, Vector4 cascadeBias, int cascadeCount,
        Vector3 sunDirection, Vector3 sunColor,
        int width, int height, ref Matrix4 view, ref Matrix4 projection, float skyExposure) {
        if (!Available || program == 0)
            return colorTexture;
        if (scene.InstanceCount == 0 || scene.SlotCount == 0)
            return colorTexture; // nothing baked / nothing to trace — no change to the scene

        // ---- 1. Half-res compute gather (rgb = off-screen indirect, a = validity) ----
        int halfW = Math.Max(1, width / 2);
        int halfH = Math.Max(1, height / 2);
        if (output.Ensure(halfW, halfH)) { /* reused */ } // (re)allocates on size change; ignore loss
        outW = halfW;
        outH = halfH;

        Matrix4 invProjection = Matrix4.Invert(projection);
        Matrix4 invView = Matrix4.Invert(view);

        GL.UseProgram(program);

        // Output image (rgba16f, writeonly). Bind the pass-owned 2D RGBA16F texture as image 0.
        GL.BindImageTexture(OutGiImageUnit, output.Texture, 0, false, 0,
            TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);

        // G-buffer + irradiance + atlas samplers on units 1..4. Set the sampler uniforms (the shader
        // uses layout(binding=) but setting them explicitly is harmless and robust to driver quirks).
        BindSampler(DepthUnit, TextureTarget.Texture2D, depthTex);
        BindSampler(NormalUnit, TextureTarget.Texture2D, normalTex);
        BindSampler(IrradianceUnit, TextureTarget.TextureCubeMap, irradianceCubemap);
        BindSampler(AtlasUnit, TextureTarget.Texture3D, atlas.TextureId);
        // Cascaded shadow map (depth texture ARRAY, sampled as sampler2DArrayShadow — the compare
        // mode lives in the texture's parameters, set when the shadow map was created).
        BindSampler(ShadowUnit, TextureTarget.Texture2DArray, shadowMapArray);

        // SDF scene SSBOs (binding 8 = instances, 9 = slot table).
        scene.Bind();

        // Plain uniforms set per-dispatch (NOT a UBO — the PassData UBO at binding 0 is off-limits).
        GL.UniformMatrix4(locInvProjection, false, ref invProjection);
        GL.UniformMatrix4(locInvView, false, ref invView);
        GL.Uniform1(locInstanceCount, (uint)scene.InstanceCount);
        GL.Uniform2(locHalfSize, halfW, halfH);
        GL.Uniform1(locSkyExposure, skyExposure);
        GL.Uniform1(locFrameIndex, frameIndex);
        GL.Uniform1(locDiagMode, diagMode ? 1 : 0);

        // Direct-sun-at-hit lighting (the bright bounce source). Same cascade/sun data the
        // volumetric march uses, so the SDF hit's shadowing matches the lit pass.
        int cascades = Math.Min(cascadeCount, locCascadeMatrices.Length);
        for (var i = 0; i < cascades; i++)
            GL.UniformMatrix4(locCascadeMatrices[i], false, ref cascadeMatrices[i]);
        GL.Uniform4(locCascadeBias, cascadeBias);
        GL.Uniform1(locCascadeCount, cascades);
        GL.Uniform3(locSunDir, sunDirection);
        GL.Uniform3(locSunColor, sunColor);
        GL.Uniform1(locHitAlbedo, HitAlbedo);
        frameIndex++;

        int gx = (halfW + 7) / 8;
        int gy = (halfH + 7) / 8;
        GL.DispatchCompute(gx, gy, 1);

        // The composite reads OutGi as a sampled texture and the SSBOs are done — barrier on image
        // access + texture fetch + storage so the writes are visible before the combine samples them.
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit |
                         MemoryBarrierFlags.TextureFetchBarrierBit |
                         MemoryBarrierFlags.ShaderStorageBarrierBit);

        // ---- 2. Full-res depth-aware upsample + additive composite onto the lit colour ----
        // A transient pooled target (released wholesale in EndFrame); the upsample needs full res.
        GLRenderTexture combined = GLRenderTexturePool.Shared.Acquire(width, height);

        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.Blend);

        combined.BindAsTarget();
        combineShader.Activate();
        BindCombineSampler(0, colorTexture, "sceneTexture");
        BindCombineSampler(1, output.Texture, "giTexture");
        BindCombineSampler(2, depthTex, "depthTexture");
        combineShader.SetMatrix4("InvProjection", ref invProjection);
        combineShader.SetFloat("SdfGiIntensity", sdfGiIntensity);
        combineShader.SetBool("DebugView", debugView);
        GLBufferUtilities.DrawFullscreenQuad();

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        return combined.Texture;
    }

    static void BindSampler(int unit, TextureTarget target, int texture) {
        GL.ActiveTexture(TextureUnit.Texture0 + unit);
        GL.BindTexture(target, texture);
    }

    void BindCombineSampler(int unit, int texture, string name) {
        GL.ActiveTexture(TextureUnit.Texture0 + unit);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        combineShader.SetInt(name, unit);
    }

    static int CompileCompute(string src) {
        int shader = GL.CreateShader(ShaderType.ComputeShader);
        // Sanitize multibyte chars (an em-dash in a comment truncates GL.ShaderSource → "EOF").
        GL.ShaderSource(shader, GLSLShaderUtilities.ToAscii(src));
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0) {
            Debugging.LogError("[GLSdfGiPass] SdfTrace_Comp compile failed:\n" +
                               GL.GetShaderInfoLog(shader));
            GL.DeleteShader(shader);
            return 0;
        }
        int prog = GL.CreateProgram();
        GL.AttachShader(prog, shader);
        GL.LinkProgram(prog);
        GL.GetProgram(prog, GetProgramParameterName.LinkStatus, out int lok);
        GL.DeleteShader(shader);
        if (lok == 0) {
            Debugging.LogError("[GLSdfGiPass] SdfTrace_Comp link failed:\n" +
                               GL.GetProgramInfoLog(prog));
            GL.DeleteProgram(prog);
            return 0;
        }
        return prog;
    }

    public void Dispose() {
        if (program != 0) {
            GL.DeleteProgram(program);
            program = 0;
        }
        atlas?.Dispose();
        scene?.Dispose();
        output.Dispose();
        meshSlots.Clear();
        Available = false;
    }

    // Adapts GLSdfAtlas (which exposes its CPU Slots list) to the GLSdfScene.ISdfAtlas seam the
    // uploader consumes. Keeps both components untouched: GLSdfScene stays decoupled from the
    // concrete atlas, GLSdfAtlas needn't know about the GPU slot-record struct.
    sealed class AtlasAdapter : GLSdfScene.ISdfAtlas {
        readonly GLSdfAtlas atlas;
        public AtlasAdapter(GLSdfAtlas atlas) => this.atlas = atlas;

        public int SlotCount => atlas.Slots.Count;

        public GLSdfScene.SdfSlotGpu SlotAt(int slot) {
            GLSdfAtlas.SdfSlot s = atlas.Slots[slot];
            // Field-for-field 1:1 (atlas texel offset, grid res, mesh-local bounds), exactly the
            // mapping the SsdfSlot std430 record + the march's SampleSlot expect.
            return new GLSdfScene.SdfSlotGpu(s.AtlasOffset, s.Res, s.BoundsMin, s.BoundsMax);
        }
    }
}
