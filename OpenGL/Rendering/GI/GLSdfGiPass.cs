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
    // Per-SUBMESH SDF resolution (longest-axis cell count). Each submesh gets its OWN tight field
    // instead of one coarse whole-scene brick — the fix for both the exterior spurious-hit wash and
    // the missing interior occlusion. 24^3 is fine enough for occlusion yet small enough (~27KB R16F)
    // to pack hundreds into the atlas.
    const int BakeResolution = 24;

    // Hard cap on distinct submesh SDFs in the atlas (Bistro has ~1600 submeshes; we can't bake all).
    // Overflow logs once and is skipped — those submeshes contribute no off-screen GI; never a crash.
    const int MaxDistinctMeshes = 512;

    // Skip submeshes whose model-space AABB diagonal is below this (metres): tiny props/detail add
    // negligible bounce but would burn atlas slots. Keeps the cap spent on the big occluders.
    const float MinSubMeshSize = 0.75f;

    // Max submesh SDFs to BAKE per frame. The bake is synchronous on the GL thread (the atlas upload
    // must be), so baking all ~512 at once was a 149ms first-frame stall. Amortizing a handful per
    // frame spreads it over a fraction of a second — the GI just fades in over the warm-up frames.
    const int MaxBakesPerFrame = 24;

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
    int locGridMin, locGridInvCell, locGridRes;
    readonly int[] locCascadeMatrices = new int[4];

    // Half-res RGBA16F gather output (pass-owned — small, consumed same frame; not pooled because
    // we bind it as an image, so a stable id is required). The full-res composite goes to a pooled
    // transient target (acquired per frame, released wholesale in EndFrame).
    readonly GLRenderTexture output = new();
    int outW, outH;

    // ---- Temporal accumulation (P7.1) — the main noise win for the 4-ray gather ----
    // The gather is a noisy ~1-spp estimate; reprojecting + EMA-accumulating last frame's result
    // (rejecting disoccluded history by view-depth, exactly the SSGI temporal pattern) averages the
    // grain to a clean image over a few frames. We reuse SSGI_Temporal.glsl verbatim: it takes the
    // raw gather as currentGI and outputs accumulated GI (rgb) + history length (a) via MRT, plus
    // the current view-depth for next frame's disocclusion test.
    readonly StandardShader temporalShader;
    readonly GLRenderTexture[] historyGi = { new(), new() };     // ping-pong accumulated GI
    readonly GLRenderTexture[] historyDepth = { new(), new() };  // ping-pong view-space depth
    int historyWrite;            // which of the two buffers to write this frame
    bool hasHistory;             // false on first frame / after a resize
    Matrix4 prevViewProjection;  // last frame's UN-jittered world->clip (for reprojection)
    int temporalFbo;
    // Accumulation window: higher = smoother + laggier. 16 is plenty to flush the 4-ray grain on a
    // static camera while staying responsive when the view moves (disocclusion shortens it anyway).
    const float MaxHistory = 16f;

    // (Mesh.InstanceId, submeshIndex) -> atlas slot index. The bake/upload done-set: a submesh in
    // this map is already packed into the atlas and never re-baked. -1 marks one that was skipped
    // (too small, failed to bake, or didn't fit) — skip silently on later frames.
    readonly record struct SubMeshKey(Guid Mesh, int SubMesh);
    readonly Dictionary<SubMeshKey, int> meshSlots = new();
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

        // Reuse the SSGI temporal accumulator verbatim (P7.1 — the main noise win).
        temporalShader = GraphicAPI.CreateStandardShader(
            EmbeddedShaderSource.Read("FSQ_Vert.glsl"),
            EmbeddedShaderSource.Read("SSGI_Temporal.glsl"));

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
        locGridMin = GL.GetUniformLocation(program, "GridMin");
        locGridInvCell = GL.GetUniformLocation(program, "GridInvCell");
        locGridRes = GL.GetUniformLocation(program, "GridRes");
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
    int bakesThisFrame;

    public void EnsureBaked(IReadOnlyList<IStaticMeshRenderer> opaque) {
        if (!Available || opaque == null)
            return;

        bakesThisFrame = 0; // reset the per-frame bake budget (amortizes the first-frame stall)

        // ---- Bake + pack any not-yet-seen submeshes; rebuild the instance list ----
        // PER-SUBMESH: each submesh of a renderer gets its own tight SDF brick. A whole-mesh
        // renderer (SubMeshIndex < 0, e.g. Bistro) contributes ALL its submeshes; a per-object
        // renderer (SubMeshIndex >= 0) contributes just that one. Vertices are MODEL space, so the
        // field's local space == model space and the GPU instance uses the renderer's WorldMatrix.
        instances.Clear();
        for (var i = 0; i < opaque.Count; i++) {
            IStaticMeshRenderer r = opaque[i];
            if (r is not { IsRenderable: true, IsActive: true })
                continue;
            Mesh mesh = r.SharedMesh;
            if (mesh == null || mesh.Vertices is not { Length: > 0 } || mesh.Indices is not { Length: > 0 })
                continue;
            Transform t = r.Transform;
            if (t == null)
                continue;
            Matrix4 world = t.WorldMatrix;

            int subCount = mesh.SubMeshes?.Length ?? 0;
            if (subCount == 0)
                continue;

            // Which submeshes does this renderer draw? -1 = all; >=0 = just that one.
            int from = r.SubMeshIndex >= 0 ? r.SubMeshIndex : 0;
            int to = r.SubMeshIndex >= 0 ? r.SubMeshIndex + 1 : subCount;
            if (from < 0 || to > subCount)
                continue;

            for (int s = from; s < to; s++) {
                int slot = SlotForSubMesh(mesh, s);
                if (slot < 0)
                    continue; // skipped (too small / cap / failed) — no GI from this submesh
                instances.Add((world, slot));
            }
        }

        scene.Build(instances, atlasAdapter);
    }

    // Returns the atlas slot for one submesh, baking + packing it on first sight (keyed by
    // (mesh, submesh)). -1 = permanently skipped (too small, over the cap, or didn't fit).
    int SlotForSubMesh(Mesh mesh, int s) {
        var key = new SubMeshKey(mesh.InstanceId, s);
        if (meshSlots.TryGetValue(key, out int existing))
            return existing;

        SubMeshData sm = mesh.SubMeshes[s];
        if (sm.IndexCount < 3) { meshSlots[key] = -1; return -1; }

        // Skip negligible submeshes so the atlas budget goes to real occluders.
        mesh.GetSubMeshBounds(s, out Vector3 lo, out Vector3 hi);
        if ((hi - lo).Length < MinSubMeshSize) { meshSlots[key] = -1; return -1; }

        if (meshSlots.Count >= MaxDistinctMeshes) {
            if (!overflowLogged) {
                Debugging.Log($"[GLSdfGiPass] submesh-SDF cap {MaxDistinctMeshes} reached; remaining " +
                              "submeshes contribute no off-screen GI (raise the cap or merge fields).");
                overflowLogged = true;
            }
            meshSlots[key] = -1;
            return -1;
        }

        // Out of per-frame bake budget: DON'T record a slot — return -1 for this frame only so the
        // submesh is retried next frame. The GI fades in over a few warm-up frames instead of one
        // big stall.
        if (bakesThisFrame >= MaxBakesPerFrame)
            return -1;
        bakesThisFrame++;

        MeshSdf sdf = MeshSdfBaker.BakeSubMesh(mesh.Vertices, mesh.Indices,
            sm.IndexStart, sm.IndexCount, new MeshSdfBaker.Settings(BakeResolution));
        if (sdf == null || !atlas.TryAdd(sdf, out int slot)) {
            meshSlots[key] = -1; // bake failed or atlas full (TryAdd logged)
            return -1;
        }
        meshSlots[key] = slot;
        return slot;
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
        int width, int height, ref Matrix4 view, ref Matrix4 projection,
        ref Matrix4 projectionNoJitter, float skyExposure) {
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
        // Instance grid mapping (the march loops only the current cell's instances).
        Vector3 gMin = scene.GridMin, gInv = scene.GridInvCell;
        GL.Uniform3(locGridMin, gMin.X, gMin.Y, gMin.Z);
        GL.Uniform3(locGridInvCell, gInv.X, gInv.Y, gInv.Z);
        GL.Uniform1(locGridRes, scene.GridResolution);
        frameIndex++;

        int gx = (halfW + 7) / 8;
        int gy = (halfH + 7) / 8;
        GL.DispatchCompute(gx, gy, 1);

        // The temporal pass reads OutGi as a sampled texture and the SSBOs are done — barrier on
        // image access + texture fetch + storage so the writes are visible before it samples them.
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit |
                         MemoryBarrierFlags.TextureFetchBarrierBit |
                         MemoryBarrierFlags.ShaderStorageBarrierBit);

        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.Blend);

        // ---- 2. Temporal accumulation (reproject + EMA, SSGI_Temporal verbatim) ----
        // Reprojects last frame's accumulated GI to this frame, rejects disoccluded history by
        // view-depth, and EMA-blends — averaging the 4-ray grain to a clean image over a few frames.
        // Uses the UN-jittered projection for reprojection (the accumulated image is jitter-free).
        // The DIAG hit-fraction view bypasses accumulation (it's not radiance) — composite raw.
        int giForComposite = output.Texture;
        if (!diagMode) {
            Matrix4 invProjNoJitter = Matrix4.Invert(projectionNoJitter);
            Matrix4 viewProjNoJitter = view * projectionNoJitter;

            int readSlot = historyWrite;
            int writeSlot = 1 - readSlot;
            GLRenderTexture giRead = historyGi[readSlot];
            GLRenderTexture giWriteTex = historyGi[writeSlot];
            GLRenderTexture depthReadTex = historyDepth[readSlot];
            GLRenderTexture depthWriteTex = historyDepth[writeSlot];

            // A resize invalidates accumulated history (reprojection would smear).
            bool sizeKept = giWriteTex.Ensure(halfW, halfH);
            giRead.Ensure(halfW, halfH);
            depthWriteTex.Ensure(halfW, halfH);
            depthReadTex.Ensure(halfW, halfH);
            if (!sizeKept)
                hasHistory = false;

            if (temporalFbo == 0)
                temporalFbo = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, temporalFbo);
            GL.Viewport(0, 0, halfW, halfH);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, giWriteTex.Texture, 0);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1,
                TextureTarget.Texture2D, depthWriteTex.Texture, 0);
            GL.DrawBuffers(2, new[] { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1 });

            temporalShader.Activate();
            BindCombineSampler(0, output.Texture, "currentGI");
            BindCombineSampler(1, giRead.Texture, "historyGI");
            BindCombineSampler(2, depthTex, "depthTexture");
            BindCombineSampler(3, normalTex, "normalTexture");
            BindCombineSampler(4, depthReadTex.Texture, "historyDepth");
            temporalShader.SetMatrix4("InvProjection", ref invProjNoJitter);
            temporalShader.SetMatrix4("InvViewMatrix", ref invView);
            temporalShader.SetMatrix4("PrevViewProjection", ref prevViewProjection);
            temporalShader.SetBool("HasHistory", hasHistory);
            temporalShader.SetFloat("MaxHistory", MaxHistory);
            GLBufferUtilities.DrawFullscreenQuad();

            // Restore single-target draw state before the composite.
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1,
                TextureTarget.Texture2D, 0, 0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0);

            giForComposite = giWriteTex.Texture;

            // Advance temporal state for next frame.
            historyWrite = writeSlot;
            hasHistory = true;
            prevViewProjection = viewProjNoJitter;
        }

        // ---- 3. Full-res depth-aware upsample + additive composite onto the lit colour ----
        // A transient pooled target (released wholesale in EndFrame); the upsample needs full res.
        GLRenderTexture combined = GLRenderTexturePool.Shared.Acquire(width, height);

        combined.BindAsTarget();
        combineShader.Activate();
        BindCombineSampler(0, colorTexture, "sceneTexture");
        BindCombineSampler(1, giForComposite, "giTexture");
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
        foreach (GLRenderTexture t in historyGi) t.Dispose();
        foreach (GLRenderTexture t in historyDepth) t.Dispose();
        if (temporalFbo != 0) { GL.DeleteFramebuffer(temporalFbo); temporalFbo = 0; }
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
