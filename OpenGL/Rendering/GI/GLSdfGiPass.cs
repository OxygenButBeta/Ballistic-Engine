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
    // Hoisted so the per-frame temporal MRT setup doesn't allocate an array every frame.
    static readonly DrawBuffersEnum[] Mrt2 = { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1 };

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
    // 64 (was 24): per-OBJECT scenes (BistroInterior, ~2000 submeshes) converged far too slowly at 24
    // (only ~48 bricks after 1500 frames -> near-empty SDF). 64/frame fills the 512 budget in ~8
    // frames; each bake is ~1ms BVH so the warm-up stays well under a stutter.
    const int MaxBakesPerFrame = 64;

    // Max instances to radiance-inject per frame (round-robin). The inject's per-voxel bounce-gather
    // is the heaviest GI cost; amortizing it across frames keeps per-frame cost bounded while the
    // EMA cache still converges (static view) / refreshes within a few frames (moving view).
    const int MaxInjectsPerFrame = 96;

    // Image unit / sampler units the compute uses (match SdfTrace_Comp.glsl layout(binding=...)):
    //   image 0 = OutGi, sampler 1 = Depth, 2 = Normal, 3 = Irradiance cube, 4 = SDF atlas.
    const int OutGiImageUnit = 0;
    const int DepthUnit = 1;
    const int NormalUnit = 2;
    const int IrradianceUnit = 3;
    const int AtlasUnit = 4;
    const int ShadowUnit = 5;   // sampler2DArrayShadow — direct-sun visibility at SDF hits
    const int SceneColorUnit = 6; // lit scene colour — fallback radiance for unfilled hits
    const int RadianceUnit = 7;   // surface-cache radiance atlas (3D) — stable per-surface radiance

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

    // LUMEN PHASE 1 — Global Distance Field clipmap. When enabled (BALLISTIC_LUMEN_GDF=1, or always
    // once Phase 1 is the default) the march samples this ONE merged field instead of the per-mesh
    // brick grid, so fragmented per-object scenes get full coverage. Built lazily with the rest.
    GLGlobalSdf globalSdf;
    static readonly bool UseGlobalSdf =
        Environment.GetEnvironmentVariable("BALLISTIC_LUMEN_GDF") == "1";

    // Not readonly: built lazily in EnsureAvailable (the first time Lumen is wanted), not the ctor.
    GLSdfAtlas atlas;
    GLSdfScene scene;
    AtlasAdapter atlasAdapter;

    int program;        // SdfTrace_Comp — the march
    int injectProgram;  // RadianceInject_Comp — fills the surface-cache radiance atlas
    // Inject uniform locations.
    int liInstanceIndex, liSkyExposure, liFeedback, liCascadeBias, liCascadeCount, liSunDir, liSunColor;
    int liInstanceCount, liGridMin, liGridInvCell, liGridRes;
    readonly int[] liCascadeMatrices = new int[4];

    // Full-res depth-aware upsample + additive composite (SdfGi_Combine.glsl). Mirrors SSR_Combine
    // but ADDS (GI only lifts, never darkens) and returns the modified litColor, so the pass plugs
    // in like GLSSGIPass — no Frag.glsl change needed.
    StandardShader combineShader;

    // BALLISTIC_SDFGI_DEBUG=1: the composite outputs ONLY the gathered GI so an enclosed-scene
    // screenshot shows the raw off-screen bounce.
    bool debugView;

    // BALLISTIC_SDFGI_DIAG=1: the gather outputs the per-pixel HIT FRACTION (grayscale) instead of
    // radiance — white = every ray hit SDF geometry, black = every ray escaped to sky. Forces the
    // debug composite so the raw value is visible. Disambiguates "rays miss" (granularity) from
    // "radiance too dim" (needs surface cache). Implies debugView.
    bool diagMode;

    // LUMEN OCTAHEDRAL SCREEN PROBES (Phase 4b). The march runs in ProbeOctMode at the probe-atlas
    // resolution (ProbeGrid * OctRes), tracing one hemisphere direction per atlas texel into probeAtlas;
    // the integrate shader then BRDF-weights each probe's octmap onto the half-res output. ~ProbeStep^2
    // fewer traces + the integrate/temporal kill the per-pixel gather noise. Default on with the GDF.
    const int OctRes = 8;       // octahedral tile edge per probe
    const int ProbeStep = 8;    // half-res pixels per probe edge (=> ~16 full-res px / probe)
    static readonly bool UseProbes =
        Environment.GetEnvironmentVariable("BALLISTIC_LUMEN_PROBES") != "0"; // default ON
    readonly GLRenderTexture probeAtlas = new();  // RGBA16F octahedral radiance atlas
    StandardShader probeIntegrateShader;
    int probeIntFbo;
    // ProbeOct uniform locations on the march program.
    int locProbeOctMode, locProbeAtlasSize, locOctRes, locProbeStep;

    // Cached uniform locations (looked up once after the link).
    int locInvProjection, locInvView, locInstanceCount, locHalfSize, locSkyExposure, locFrameIndex, locDiagMode;
    int locCascadeBias, locCascadeCount, locSunDir, locSunColor, locHitAlbedo;
    int locGridMin, locGridInvCell, locGridRes, locViewProj;
    readonly int[] locCascadeMatrices = new int[4];
    // Global distance field (Phase 1) + radiance clipmap (Phase 2) uniform locations.
    int locUseGlobalSdf, locGlobalSdfRes, locUseGlobalRadiance;
    readonly int[] locGlobalSdf = new int[GLGlobalSdf.CascadeCount];
    readonly int[] locGlobalSdfMin = new int[GLGlobalSdf.CascadeCount];
    readonly int[] locGlobalSdfCell = new int[GLGlobalSdf.CascadeCount];
    readonly int[] locGlobalRadiance = new int[GLGlobalSdf.CascadeCount];

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
    StandardShader temporalShader;
    // Edge-aware a-trous spatial denoise (SSGI_Denoise.glsl, reused): smooths the within-frame
    // grazing-surface speckle the temporal pass can't resolve (a fixed per-pixel screen-space-read
    // accept/reject pattern), with depth/normal edge-stops so it doesn't bleed across corners.
    StandardShader denoiseShader;
    readonly GLRenderTexture[] denoisePingPong = { new(), new() };
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
    readonly List<(Matrix4 world, int slot, Vector3 albedo, Vector3 emissive)> instances = new();
    // Scratch bake-candidate list, sorted largest-first so the atlas budget goes to big occluders.
    readonly List<(IStaticMeshRenderer r, Mesh mesh, Matrix4 world, int sub, float worldSize)> bakeCandidates = new();

    int frameIndex;

    bool buildAttempted;   // lazy-build guard: a failed build (e.g. compile error) won't retry forever

    public GLSdfGiPass() {
        // Lazy build now: the GPU resources are created the FIRST time the pass is actually requested
        // (EnsureAvailable), NOT only when BALLISTIC_SDFGI=1 at startup. The old constructor built only
        // under the env var and set Available=false forever otherwise — so turning Lumen on via the
        // volume override at runtime did NOTHING (the resources never existed). If the env var IS set,
        // build eagerly so the very first frame already has it; otherwise wait for the override.
        if (Environment.GetEnvironmentVariable("BALLISTIC_SDFGI") == "1")
            EnsureAvailable();
    }

    // Builds the SDF-GI GPU resources on demand (idempotent). Returns Available. Called by the renderer
    // when the pass is wanted — either the env var (eager, in the ctor) or the Lumen/GlobalIllumination
    // volume override flipping it on at runtime. Runs on the GL thread (the renderer's render loop).
    public bool EnsureAvailable() {
        if (Available || buildAttempted)
            return Available;
        buildAttempted = true;

        diagMode = Environment.GetEnvironmentVariable("BALLISTIC_SDFGI_DIAG") == "1";
        debugView = diagMode || Environment.GetEnvironmentVariable("BALLISTIC_SDFGI_DEBUG") == "1";

        atlas = new GLSdfAtlas(GLSdfAtlas.DefaultSize);
        scene = new GLSdfScene();
        atlasAdapter = new AtlasAdapter(atlas);

        program = CompileCompute(EmbeddedShaderSource.Read("SdfTrace_Comp.glsl"));
        injectProgram = CompileCompute(EmbeddedShaderSource.Read("RadianceInject_Comp.glsl"));
        if (program == 0 || injectProgram == 0) {
            // Compile/link failed (logged by CompileCompute). Stay unavailable; clean up what we made.
            atlas.Dispose();
            scene.Dispose();
            Available = false;
            return false;
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
        // Reuse the SSGI a-trous spatial denoise (kills the residual grazing-surface speckle).
        denoiseShader = GraphicAPI.CreateStandardShader(
            EmbeddedShaderSource.Read("FSQ_Vert.glsl"),
            EmbeddedShaderSource.Read("SSGI_Denoise.glsl"));

        CacheUniformLocations();
        probeIntegrateShader = GraphicAPI.CreateStandardShader(
            EmbeddedShaderSource.Read("FSQ_Vert.glsl"),
            EmbeddedShaderSource.Read("LumenProbeIntegrate_Frag.glsl"));
        if (UseGlobalSdf)
            // 96^3 over a 12m base cascade = 0.125m near-field cells (Lumen-class fine; 64^3/16m was
            // 0.25m and blended adjacent walls into grey on small scenes). Outer cascades (x2 each)
            // still reach ~96m for the far field. Background-baked so the higher res isn't a stall.
            globalSdf = new GLGlobalSdf(resolution: 96, baseExtent: 12f);
        Console.WriteLine($"[SdfGI] resources built (Lumen enabled{(UseGlobalSdf ? ", GLOBAL distance field" : "")}).");
        Available = true;
        return true;
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
        locViewProj = GL.GetUniformLocation(program, "ViewProj");
        for (var i = 0; i < 4; i++)
            locCascadeMatrices[i] = GL.GetUniformLocation(program, CascadeMatrixNames[i]);

        // Global distance field (Phase 1): per-cascade sampler + placement uniforms (array elements).
        locProbeOctMode = GL.GetUniformLocation(program, "ProbeOctMode");
        locProbeAtlasSize = GL.GetUniformLocation(program, "ProbeAtlasSize");
        locOctRes = GL.GetUniformLocation(program, "OctRes");
        locProbeStep = GL.GetUniformLocation(program, "ProbeStep");
        locUseGlobalSdf = GL.GetUniformLocation(program, "UseGlobalSdf");
        locUseGlobalRadiance = GL.GetUniformLocation(program, "UseGlobalRadiance");
        locGlobalSdfRes = GL.GetUniformLocation(program, "GlobalSdfRes");
        for (var i = 0; i < GLGlobalSdf.CascadeCount; i++) {
            locGlobalSdf[i] = GL.GetUniformLocation(program, $"GlobalSdf[{i}]");
            locGlobalSdfMin[i] = GL.GetUniformLocation(program, $"GlobalSdfMin[{i}]");
            locGlobalSdfCell[i] = GL.GetUniformLocation(program, $"GlobalSdfCell[{i}]");
            locGlobalRadiance[i] = GL.GetUniformLocation(program, $"GlobalRadiance[{i}]");
        }

        // Inject program uniforms.
        liInstanceIndex = GL.GetUniformLocation(injectProgram, "InstanceIndex");
        liSkyExposure = GL.GetUniformLocation(injectProgram, "SkyExposure");
        liFeedback = GL.GetUniformLocation(injectProgram, "Feedback");
        liCascadeBias = GL.GetUniformLocation(injectProgram, "CascadeBias");
        liCascadeCount = GL.GetUniformLocation(injectProgram, "CascadeCount");
        liSunDir = GL.GetUniformLocation(injectProgram, "SunDirectionWorld");
        liSunColor = GL.GetUniformLocation(injectProgram, "SunColor");
        liInstanceCount = GL.GetUniformLocation(injectProgram, "InstanceCount");
        liGridMin = GL.GetUniformLocation(injectProgram, "GridMin");
        liGridInvCell = GL.GetUniformLocation(injectProgram, "GridInvCell");
        liGridRes = GL.GetUniformLocation(injectProgram, "GridRes");
        for (var i = 0; i < 4; i++)
            liCascadeMatrices[i] = GL.GetUniformLocation(injectProgram, CascadeMatrixNames[i]);
    }

    // Bakes + uploads the SDF for every distinct mesh in the opaque set (once each, keyed by
    // Mesh.InstanceId), then rebuilds the GLSdfScene instance SSBO from the current per-renderer
    // world transforms. Cheap after warm-up: no re-bake, just the instance-list rebuild + upload.
    //
    // Bake happens on the calling (GL) thread here — TryAdd issues GL calls so the upload must be on
    // the GL thread, and the bake is bounded (BakeResolution^3, BVH-accelerated, one-time per mesh).
    // For very large meshes a future pass can move the CPU bake off-thread and only TryAdd here.
    int bakesThisFrame;
    int injectCursor;   // round-robin start index for the per-frame amortized radiance inject

    public void EnsureBaked(IReadOnlyList<IStaticMeshRenderer> opaque, Vector3 cameraPos = default) {
        // Phase 1: advance the global distance field clipmap (one cascade per frame, background bake).
        // Camera-centered, so it follows the view; re-bakes a cascade on scroll / geometry change.
        if (UseGlobalSdf) {
            globalSdf?.Update(cameraPos, opaque);
            return; // GDF replaces the per-mesh bricks entirely — skip the per-submesh bake + grid build
        }
        if (!Available || opaque == null)
            return;

        bakesThisFrame = 0; // reset the per-frame bake budget (amortizes the first-frame stall)

        // ---- Bake + pack any not-yet-seen submeshes; rebuild the instance list ----
        // PER-SUBMESH: each submesh of a renderer gets its own tight SDF brick. A whole-mesh
        // renderer (SubMeshIndex < 0, e.g. Bistro) contributes ALL its submeshes; a per-object
        // renderer (SubMeshIndex >= 0) contributes just that one. Vertices are MODEL space, so the
        // field's local space == model space and the GPU instance uses the renderer's WorldMatrix.
        // Collect every candidate submesh with its WORLD-SPACE size FIRST, then bake LARGEST-FIRST.
        // CRITICAL for per-object scenes (BistroInterior is ~796 separate renderers — chairs, bottles,
        // cutlery): the atlas cap (MaxDistinctMeshes) is small relative to the submesh count, and the
        // OLD code baked in arbitrary scene order, so the budget was spent on tiny props while the big
        // occluders that actually shape GI (walls, floor, ceiling, bar) never got bricks — the march
        // then escaped to nothing and the gather was ZERO (proven: BistroInterior raw gather mean 0 vs
        // SunTemple's 101). Baking biggest-first puts the budget where it matters. (A whole-mesh scene
        // like SunTemple is unaffected — it has one renderer and its submeshes already fit.)
        bakeCandidates.Clear();
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

            int from = r.SubMeshIndex >= 0 ? r.SubMeshIndex : 0;
            int to = r.SubMeshIndex >= 0 ? r.SubMeshIndex + 1 : subCount;
            if (from < 0 || to > subCount)
                continue;

            // World-space scale: the local-bounds diagonal scaled by the transform's lossy scale, so a
            // big-but-far prop still ranks by its real size. Cheap proxy good enough for ordering.
            float scaleLen = world.ExtractScale().Length;
            for (int s = from; s < to; s++) {
                mesh.GetSubMeshBounds(s, out Vector3 lo, out Vector3 hi);
                float worldSize = (hi - lo).Length * scaleLen;
                bakeCandidates.Add((r, mesh, world, s, worldSize));
            }
        }
        // Largest occluders first. Already-baked submeshes (in meshSlots) short-circuit in SlotForSubMesh,
        // so re-sorting every frame is cheap and lets a newly-seen big piece still get priority.
        bakeCandidates.Sort((a, b) => b.worldSize.CompareTo(a.worldSize));

        instances.Clear();
        foreach (var (r, mesh, world, s, _) in bakeCandidates) {
            Material mat = r.MaterialFor(s);
            int slot = SlotForSubMesh(mesh, s, mat);
            if (slot < 0)
                continue; // skipped (too small / cap / failed / cutout) — no GI from this submesh
            Vector3 albedo = mat != null ? mat.BaseColorFactor.Xyz : new Vector3(0.5f);
            Vector3 emissive = mat is { IsEmissive: true }
                ? mat.EmissiveColor * mat.EmissiveIntensity : Vector3.Zero;
            instances.Add((world, slot, albedo, emissive));
        }

        scene.Build(instances, atlasAdapter);
    }

    // Returns the atlas slot for one submesh, baking + packing it on first sight (keyed by
    // (mesh, submesh)). -1 = permanently skipped (too small, cutout, over the cap, or didn't fit).
    int SlotForSubMesh(Mesh mesh, int s, Material mat) {
        var key = new SubMeshKey(mesh.InstanceId, s);
        if (meshSlots.TryGetValue(key, out int existing))
            return existing;

        SubMeshData sm = mesh.SubMeshes[s];
        if (sm.IndexCount < 3) { meshSlots[key] = -1; return -1; }

        // EXCLUDE CUTOUT (alpha-tested) materials — foliage, garlands, ivy, grates. Their geometry
        // is THIN SHELLS that a coarse 24^3 SDF can't represent (the field becomes a noisy blob), so
        // gather rays near them randomly hit/miss the garbage SDF and read dark/zero radiance — the
        // black salt-and-pepper cloud (proven: it sat exactly on SunTemple's wreath + column ivy, and
        // vanished with SDF-GI off). This is the Lumen approach: foliage is excluded from SDF tracing
        // (Lumen handles it via screen traces / cards instead). Skipped here = no off-screen bounce
        // FROM these surfaces, but they still receive GI and the speckle is gone.
        if (mat is { Cutout: true }) { meshSlots[key] = -1; return -1; }

        // Skip negligible submeshes so the atlas budget goes to real occluders.
        mesh.GetSubMeshBounds(s, out Vector3 lo, out Vector3 hi);
        if ((hi - lo).Length < MinSubMeshSize) { meshSlots[key] = -1; return -1; }

        // Cap on BAKED slots, not dict entries. The dict also holds the -1 SKIP markers (tiny/cutout/
        // failed), so counting meshSlots.Count hit the cap at 512 ENTRIES — mostly skips — leaving only
        // a couple dozen real bricks and a near-empty SDF (BistroInterior gathered mean 0). atlas.Slots
        // is the real baked-brick count, so the budget now goes to 512 actual occluders.
        if (atlas.Slots.Count >= MaxDistinctMeshes) {
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
    // intensityScale: a runtime multiplier on the additive composite strength (on TOP of the env/
    // default sdfGiIntensity). The renderer passes < 1 when baked PROBES are active — the probes
    // already carry the bulk of the static enclosed-interior bounce, so SDF-GI AUGMENTS (adds the
    // dynamic off-screen delta the static probes can't) rather than double-counting the same light.
    public int Render(int colorTexture, int depthTex, int normalTex, int irradianceCubemap,
        int shadowMapArray, Matrix4[] cascadeMatrices, Vector4 cascadeBias, int cascadeCount,
        Vector3 sunDirection, Vector3 sunColor,
        int width, int height, ref Matrix4 view, ref Matrix4 projection,
        ref Matrix4 projectionNoJitter, float skyExposure, float intensityScale = 1f) {
        if (!Available || program == 0)
            return colorTexture;
        bool gdf = UseGlobalSdf && globalSdf is { Available: true };
        // The GDF path traces the global field (no per-mesh instances); the per-mesh path needs baked
        // instances. Bail only when the ACTIVE path has nothing to trace.
        if (!gdf && (scene.InstanceCount == 0 || scene.SlotCount == 0))
            return colorTexture;

        // ---- 0. Radiance inject (surface cache fill) ---- per-mesh path only (the GDF has no per-mesh
        // radiance slots yet — Phase 2 adds card-based surface-cache radiance; for now the GDF hit uses
        // the direct sun+sky estimate in the shader, so the inject is skipped when the GDF is active).
        if (!gdf)
            InjectRadiance(irradianceCubemap, shadowMapArray, cascadeMatrices, cascadeBias, cascadeCount,
                sunDirection, sunColor, skyExposure);

        // ---- 1. Half-res compute gather (rgb = off-screen indirect, a = validity) ----
        // ceil (not floor): full half-res coverage of an ODD full-res height (no clamped-edge upsample
        // flash at the bottom row under motion). Byte-identical for even dimensions.
        int halfW = Math.Max(1, (width + 1) / 2);
        int halfH = Math.Max(1, (height + 1) / 2);
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
        // Lit scene colour (kept as a fallback radiance source for hits the cache hasn't filled).
        BindSampler(SceneColorUnit, TextureTarget.Texture2D, colorTexture);
        // The surface-cache radiance atlas — the STABLE per-surface radiance the march reads at hits.
        BindSampler(RadianceUnit, TextureTarget.Texture3D, atlas.RadianceTextureId);

        // SDF scene SSBOs (binding 8 = instances, 9 = slot table). Per-mesh path only — the GDF path
        // doesn't use the instance grid, but binding it is harmless (the shader gates on UseGlobalSdf).
        if (!gdf)
            scene.Bind();

        // GLOBAL DISTANCE FIELD: bind the 4 cascade distance textures (units 8..11) + radiance clipmap
        // (units 12..15) and set their placement + the voxel-lighting (Phase 2) radiance read.
        const int GdfFirstUnit = 8;
        const int GdfRadFirstUnit = 12;
        GL.Uniform1(locUseGlobalSdf, gdf ? 1 : 0);
        GL.Uniform1(locUseGlobalRadiance, 0);
        if (gdf) {
            // Voxel-lighting inject (one cascade/frame): light the radiance clipmap from the GDF before
            // the march reads it. Uses the same sun/shadow/sky as the per-mesh inject; mid-grey albedo
            // (no per-voxel material in v1). Feedback 0.9 = sticky EMA for stability + multi-bounce.
            globalSdf.InjectRadiance(irradianceCubemap, shadowMapArray, cascadeMatrices, cascadeBias,
                cascadeCount, sunDirection, sunColor, new Vector3(HitAlbedo), skyExposure, 0.9f);
            GL.Uniform1(locUseGlobalRadiance, 1);

            GL.UseProgram(program); // InjectRadiance bound its own program — restore the march program
            globalSdf.Bind(GdfFirstUnit);
            for (int c = 0; c < GLGlobalSdf.CascadeCount; c++) {
                GL.Uniform1(locGlobalSdf[c], GdfFirstUnit + c);
                Vector3 mn = globalSdf.CascadeMin(c);
                GL.Uniform3(locGlobalSdfMin[c], mn.X, mn.Y, mn.Z);
                GL.Uniform1(locGlobalSdfCell[c], globalSdf.CascadeCell(c));
                // Radiance clipmap (the global surface cache the hit reads).
                GL.ActiveTexture(TextureUnit.Texture0 + GdfRadFirstUnit + c);
                GL.BindTexture(TextureTarget.Texture3D, globalSdf.RadianceRead(c));
                GL.Uniform1(locGlobalRadiance[c], GdfRadFirstUnit + c);
            }
            GL.Uniform1(locGlobalSdfRes, globalSdf.Resolution);
        }

        // Plain uniforms set per-dispatch (NOT a UBO — the PassData UBO at binding 0 is off-limits).
        GL.UniformMatrix4(locInvProjection, false, ref invProjection);
        GL.UniformMatrix4(locInvView, false, ref invView);
        // World->clip (this frame, jittered to match the depth buffer) for the screen-space radiance
        // read: project an SDF hit to screen, depth-validate, sample the lit colour there.
        Matrix4 viewProj = view * projection;
        GL.UniformMatrix4(locViewProj, false, ref viewProj);
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

        // LUMEN OCTAHEDRAL SCREEN PROBES (Phase 4b): trace at the coarse probe atlas, then BRDF-integrate
        // to the half-res output. Only the GDF path (the per-mesh path keeps the per-pixel gather).
        bool probes = gdf && UseProbes && !diagMode;
        int probeGridX = (halfW + ProbeStep - 1) / ProbeStep;
        int probeGridY = (halfH + ProbeStep - 1) / ProbeStep;
        if (probes) {
            int atlasW = probeGridX * OctRes, atlasH = probeGridY * OctRes;
            probeAtlas.Ensure(atlasW, atlasH);
            // 1. Probe trace: dispatch over the atlas, OutGi = probe atlas, ProbeOctMode=1.
            GL.BindImageTexture(OutGiImageUnit, probeAtlas.Texture, 0, false, 0,
                TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
            GL.Uniform1(locProbeOctMode, 1);
            GL.Uniform2(locProbeAtlasSize, atlasW, atlasH);
            GL.Uniform1(locOctRes, OctRes);
            GL.Uniform1(locProbeStep, ProbeStep);
            GL.DispatchCompute((atlasW + 7) / 8, (atlasH + 7) / 8, 1);
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);

            // 2. Integrate: per half-res pixel, BRDF-weighted sum of the surrounding probes' octmaps -> output.
            GL.Disable(EnableCap.DepthTest); GL.Disable(EnableCap.CullFace); GL.Disable(EnableCap.Blend);
            output.BindAsTarget();
            probeIntegrateShader.Activate();
            BindCombineSampler(0, probeAtlas.Texture, "probeAtlas");
            BindCombineSampler(1, depthTex, "depthTexture");
            BindCombineSampler(2, normalTex, "normalTexture");
            Matrix4 invP = Matrix4.Invert(projection), invV = Matrix4.Invert(view);
            probeIntegrateShader.SetMatrix4("InvProjection", ref invP);
            probeIntegrateShader.SetMatrix4("InvView", ref invV);
            probeIntegrateShader.SetInt("ProbeGridX", probeGridX);
            probeIntegrateShader.SetInt("ProbeGridY", probeGridY);
            probeIntegrateShader.SetInt("HalfDimsX", halfW);
            probeIntegrateShader.SetInt("HalfDimsY", halfH);
            probeIntegrateShader.SetInt("ProbeStep", ProbeStep);
            probeIntegrateShader.SetInt("OctRes", OctRes);
            GLBufferUtilities.DrawFullscreenQuad();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }
        else {
            GL.Uniform1(locProbeOctMode, 0);
            int gx = (halfW + 7) / 8;
            int gy = (halfH + 7) / 8;
            GL.DispatchCompute(gx, gy, 1);
        }

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
            GL.DrawBuffers(2, Mrt2);

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

            // ---- 2b. Edge-aware a-trous spatial denoise (2 iterations, widening tap spacing) ----
            // Smooths the residual grazing-surface speckle while depth/normal edge-stops keep the
            // box/wall corners crisp. Ping-pong the half-res GI through the SSGI denoiser.
            denoisePingPong[0].Ensure(halfW, halfH);
            denoisePingPong[1].Ensure(halfW, halfH);
            Matrix4 invProjNoJitterCopy = invProjNoJitter;
            GLRenderTexture src = giWriteTex;
            // 4 iterations (1,2,4,8 texel spacing). The cache-bounce GI is LOW-FREQUENCY (diffuse,
            // per-surface), so a wide a-trous is correct — it crushes the 6-ray hit/miss speckle that
            // lingers in dark recesses (where temporal alone can't, the gather variance is highest).
            // Edge stops loosened (DepthSigma 0.2, NormalSigma 16) so the blur crosses the speckle but
            // still respects real depth/normal discontinuities (column edges, corners stay crisp).
            for (var iter = 0; iter < 4; iter++) {
                GLRenderTexture dst = denoisePingPong[iter & 1];
                dst.BindAsTarget();
                denoiseShader.Activate();
                BindCombineSampler(0, src.Texture, "giTexture");
                BindCombineSampler(1, depthTex, "depthTexture");
                BindCombineSampler(2, normalTex, "normalTexture");
                denoiseShader.SetMatrix4("InvProjection", ref invProjNoJitterCopy);
                denoiseShader.SetFloat("StepSize", (float)(1 << iter)); // 1, 2, 4, 8 texel spacing
                denoiseShader.SetFloat("DepthSigma", 0.2f);
                denoiseShader.SetFloat("NormalSigma", 16f);
                GLBufferUtilities.DrawFullscreenQuad();
                src = dst;
            }
            giForComposite = src.Texture;
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
        // The probe-aware scale folds in here so SDF-GI augments (not double-counts) baked probes.
        // DebugView/diag bypass the scale so the raw gather stays inspectable at full strength.
        combineShader.SetFloat("SdfGiIntensity",
            debugView ? sdfGiIntensity : sdfGiIntensity * MathHelper.Clamp(intensityScale, 0f, 1f));
        combineShader.SetBool("DebugView", debugView);
        GLBufferUtilities.DrawFullscreenQuad();

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        return combined.Texture;
    }

    // Fills the surface-cache radiance atlas: one dispatch per instance (group counts from its brick
    // resolution), computing lit radiance at each near-surface voxel and temporally accumulating it.
    // Runs before the march each frame; the march then reads the cached radiance at hits.
    void InjectRadiance(int irradianceCubemap, int shadowMapArray, Matrix4[] cascadeMatrices,
        Vector4 cascadeBias, int cascadeCount, Vector3 sunDirection, Vector3 sunColor, float skyExposure) {
        // PING-PONG + AMORTIZATION: only a round-robin SLICE of instances is injected per frame (the
        // gather is the heavy cost). But the swap makes the WRITE volume the next readable one, so
        // the NON-injected instances' radiance must survive. Seed the write volume with last frame's
        // full read volume (a cheap whole-texture GPU copy) BEFORE injecting the slice over it — so
        // un-injected bricks keep their converged radiance and only the slice advances one bounce.
        // Copy only the USED sub-volume (all bricks live in [0, UsedDepth) — the shelf allocator fills
        // from Z=0 up), not the whole Size^3. Most of the 256^3 atlas is empty, so for a partly-filled
        // atlas this cuts the per-frame 32MB copy roughly in proportion to how much is actually used —
        // a big chunk of the SDF-GI pass cost (profiled ~4.7ms with SDF-GI on).
        int usedZ = Math.Clamp(atlas.UsedDepth, 1, atlas.Size);
        GL.CopyImageSubData(
            atlas.RadianceReadTextureId, ImageTarget.Texture3D, 0, 0, 0, 0,
            atlas.RadianceWriteTextureId, ImageTarget.Texture3D, 0, 0, 0, 0,
            atlas.Size, atlas.Size, usedZ);
        GL.MemoryBarrier(MemoryBarrierFlags.TextureUpdateBarrierBit |
                         MemoryBarrierFlags.ShaderImageAccessBarrierBit);

        GL.UseProgram(injectProgram);

        // PING-PONG: bind the WRITE volume as the image (binding 1, write-only this frame) and the
        // READ volume (last frame's converged radiance) as the sampler (binding 7). The gather + EMA
        // read the sampler only — never the image — so there's no same-frame read-during-write.
        GL.BindImageTexture(1, atlas.RadianceWriteTextureId, 0, true, 0,
            TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
        BindSampler(AtlasUnit, TextureTarget.Texture3D, atlas.TextureId);
        BindSampler(IrradianceUnit, TextureTarget.TextureCubeMap, irradianceCubemap);
        BindSampler(ShadowUnit, TextureTarget.Texture2DArray, shadowMapArray);
        BindSampler(RadianceUnit, TextureTarget.Texture3D, atlas.RadianceReadTextureId);
        scene.Bind();

        GL.Uniform1(liSkyExposure, skyExposure);
        // Cache EMA weight for the OLD value. 0.9 was TOO sticky: a brick is only re-injected every
        // ~total/MaxInjectsPerFrame frames (round-robin), so 0.9 per-inject meant ~100+ frames to
        // converge — the cache stayed sparse/under-built, the march missed, and dark recesses got
        // hard-0 black specks. 0.75 (25% new each inject) converges in a handful of injects while the
        // ping-pong keeps it stable (no same-frame feedback to amplify), filling the cache so the
        // march hits real radiance instead of the flickery screen-space miss fallback.
        GL.Uniform1(liFeedback, 0.75f);
        GL.Uniform1(liInstanceCount, (uint)scene.InstanceCount);
        Vector3 gMin = scene.GridMin, gInv = scene.GridInvCell;
        GL.Uniform3(liGridMin, gMin.X, gMin.Y, gMin.Z);
        GL.Uniform3(liGridInvCell, gInv.X, gInv.Y, gInv.Z);
        GL.Uniform1(liGridRes, scene.GridResolution);
        int cascades = Math.Min(cascadeCount, liCascadeMatrices.Length);
        for (var i = 0; i < cascades; i++)
            GL.UniformMatrix4(liCascadeMatrices[i], false, ref cascadeMatrices[i]);
        GL.Uniform4(liCascadeBias, cascadeBias);
        GL.Uniform1(liCascadeCount, cascades);
        GL.Uniform3(liSunDir, sunDirection);
        GL.Uniform3(liSunColor, sunColor);

        // One dispatch per instance — group counts from its brick resolution (local_size 4^3).
        // AMORTIZED: inject only a slice of instances per frame (round-robin via injectCursor). The
        // cache accumulates via the EMA, so a static view still fully converges and a moving view
        // refreshes over a few frames — cutting the heavy per-voxel bounce-gather cost per frame
        // (e.g. SunTemple's 512 instances spread over ~MaxInjectsPerFrame-sized batches).
        ReadOnlySpan<int> slots = scene.InstanceSlots;
        var atlasSlots = atlas.Slots;
        int total = slots.Length;
        int batch = Math.Min(total, MaxInjectsPerFrame);
        for (var k = 0; k < batch; k++) {
            int i = (injectCursor + k) % total;
            int slot = slots[i];
            if ((uint)slot >= (uint)atlasSlots.Count)
                continue;
            Vector3i res = atlasSlots[slot].Res;
            GL.Uniform1(liInstanceIndex, i);
            GL.DispatchCompute((res.X + 3) / 4, (res.Y + 3) / 4, (res.Z + 3) / 4);
        }
        injectCursor = total > 0 ? (injectCursor + batch) % total : 0;

        // Make the radiance image writes visible, then flip the ping-pong: the volume just written
        // becomes the readable one, so the march's RadianceReadTextureId sampler picks up this
        // frame's fresh radiance.
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit |
                         MemoryBarrierFlags.TextureFetchBarrierBit);
        atlas.SwapRadiance();
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
        globalSdf?.Dispose();
        output.Dispose();
        probeAtlas.Dispose();
        foreach (GLRenderTexture t in historyGi) t.Dispose();
        foreach (GLRenderTexture t in historyDepth) t.Dispose();
        foreach (GLRenderTexture t in denoisePingPong) t.Dispose();
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
