using System.Numerics;
using BallisticEngine.DX12;
using BallisticEngine.Rendering; // BatchGroup<T>
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;
using GLMatrix4 = System.Numerics.Matrix4x4; // engine math is System.Numerics now; ToNumerics(...) is an identity copy
using GLVector3 = System.Numerics.Vector3;

namespace BallisticEngine;

// The DX12 forward renderer. Minimal opaque path (first light on a real scene): iterate the scene's
// static mesh renderers, draw each submesh with its material's diffuse map under a directional N·L +
// ambient, ACES-tonemapped, into an offscreen color+depth target. NO shadows/IBL/full-PBR/post yet —
// those layer on in later milestones (Docs/Plans/dx-native-abstraction-redesign.md). This proves the
// real path end-to-end: engine mesh buffers -> input layout -> per-draw CBV + per-material SRV table ->
// depth-tested draw -> readback.
//
// Drives shading via constant buffers + descriptor tables directly (NOT the GL per-name uniform API),
// and uses NO reflection on the per-frame path (standing rule): it iterates a typed RuntimeSet and reads
// typed properties only.
public sealed class DX12HDRenderer : HDRenderer
{
    readonly Dx12Device dev;

    // The backing device — exposed so the headless render path can drain the debug/GBV info queue at
    // end-of-frame (W2 validation baseline). Read-only; the renderer still owns the device's lifetime.
    public Dx12Device Device => dev;
    Dx12OffscreenTarget target; // HDR scene color (R16F) + depth — opaque/sky/fog render here

    Dx12OffscreenTarget ldr; // LDR composite output (R8) — readback/display reads this

    // targetW/targetH = the INTERNAL (render) resolution: the scene + all post passes render here. When FSR
    // is off this equals the output resolution. When FSR is on it's the (smaller) FSR render resolution and
    // the upscaler reconstructs outputW/outputH. ldr is always at output resolution.
    int targetW = 1920, targetH = 1080;
    int outputW = 1920, outputH = 1080;

    // FSR temporal upscaling: render at targetW/H (internal) -> fsrOutput (output res). Replaces TAA when
    // active. fsrUnavailable latches if the native DLLs fail to load (clean checkout) so we stop retrying.
    Dx12FsrUpscaler fsr;
    Dx12OffscreenTarget fsrOutput; // HDR (R16F), output res, UAV-writable — FSR's reconstructed color
    bool fsrActive;
    bool fsrUnavailable;
    UpscaleMode currentUpscaleMode = UpscaleMode.Off;
    const float FovYRadians = 45f * (MathF.PI / 180f); // matches the projection's vertical FOV

    ID3D12RootSignature rootSig;
    ID3D12PipelineState pso;

    // --- Clustered-deferred path ---
    // Geometry pass: writes the fat G-buffer (4 MRT) with the same vertex transform + material sampling as
    // the old forward opaque, but NO lighting (GBuffer.hlsl). Reuses the per-draw DrawConstants CBV (b0) +
    // 6 material SRVs (t0..t5) — same root sig shape as the forward path minus the IBL/shadow/frame params.
    Dx12GBuffer gbuffer;
    ID3D12RootSignature gbufferRootSig;
    ID3D12PipelineState gbufferPso;

    // GPU SKINNING into the same G-buffer. A skinned mesh (SkinnedMeshRenderer, IsSkinned) carries 2 extra
    // vertex streams (bone indices as floats / weights) and an Animator feeds per-bone skinning matrices
    // each frame. The skinned PSO (GBufferSkinned.hlsl) skins pos/normal/tangent in the vertex stage before
    // the model transform; the pixel stage is byte-identical to GBuffer.hlsl so deferred shading matches a
    // static mesh exactly. The bone matrices ride in a per-frame upload ring bound as a root SRV (t6).
    ID3D12RootSignature skinnedGbufferRootSig;
    ID3D12PipelineState skinnedGbufferPso;
    ID3D12Resource boneMatrixRing; // upload heap: transposed float4x4[] per skinned draw
    unsafe byte* boneMatrixMapped;
    int boneMatrixSlotSize; // bytes per skinned draw (maxBones * 64, 256-aligned)
    int boneMatrixSlotCount; // skinned draws per frame ceiling
    const int MaxBonesPerDraw = 256; // skeleton bone ceiling for one skinned mesh

    // Motion vectors: a per-pass CBV (b1) shared by BOTH geometry passes (CPU GBuffer.hlsl + GPU-driven
    // GBufferBindless.hlsl) holding the UNJITTERED current + previous frame view*proj. The geometry PS
    // reprojects each surface's world position through both to write a jitter-free screen-space motion
    // vector (prevUV - currUV) into the G-buffer's RG16F motion target — consumed by TAA and the FSR
    // upscaler. Camera reprojection (correct for static geometry, which is all of the heavy test content);
    // per-object motion for animated/physics renderers is a follow-up (would bake a prev model per draw).
    ID3D12Resource motionCb;
    unsafe byte* motionCbMapped;
    Matrix4x4 motionPrevViewProj; // previous frame's UNJITTERED view*proj
    bool motionPrevValid;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct MotionConstants
    {
        public Matrix4x4 ViewProjCur;
        public Matrix4x4 ViewProjPrev;
        public float NormalLodBias;
        public Vector3 PadMotion;
    }

    // Deferred lighting: deferredRootSig/Pso/Cb/SrvVisible + the LightConstants struct moved VERBATIM into
    // Resources/Dx12DeferredLightingPass.cs (chunk 9). The pass owns them; it runs at the OpaqueLighting event
    // (300) via the graph, reading ctx light/IBL/cluster/rtShadow state. Was BuildDeferredLighting /
    // DrawDeferredLighting.

    // Clustered punctual lights (point/spot) shaded in the deferred pass — orchestrator-owned (the CPU froxel
    // gather runs inline before deferred), built in Initialize; the deferred pass reads it via ctx.
    Dx12ClusteredLights clusteredLights;

    // TAA: jittered rendering + reprojected history accumulation (the AA; also smooths SSR/SSAO noise).
    // The jitter is applied to the camera projection (whole frame); reprojection uses UNJITTERED matrices.
    // Driven by the AntiAliasing VOLUME (PostFX.TaaEnabled / TaaFeedback). The jitter offset is reused by
    // the FSR upscaler later (plumbed once here).
    // TAA — CONVERTED to a pass-graph IRenderPass (chunk 7): Dx12TaaPass owns the rootsig/PSO/CB/heap +
    // ping-pong history targets + the taaWriteB/taaHistoryValid state. Runs at PostProcess (after SSAO, before
    // composite) in the native path only (FSR replaces it). Was BuildTaa/AllocTaaTargets/DrawTaa.
    Dx12TaaPass taaPass;
    Dx12FsrPass fsrPass; // chunk 7: FSR dispatch (Record only); fsr/fsrOutput stay orchestrator-owned
    int taaFrame; // jitter phase counter (shared by TAA + FSR; advanced in the frame tail)
    int frameCounter; // monotonic frame index, advanced EVERY BeginRender (drives time-animated FX like dust drift)
    Vector2 currentJitter; // this frame's sub-pixel jitter (pixels) — exposed for FSR reuse

    // Reflections moved to Resources/Dx12ReflectionsPass.cs (Event=Reflections 600). The pass owns the
    // SSR rootsig/PSOs/targets and the optional RT reflection pipeline.

    // --- DXR ray-traced sun shadows (volume-driven: Shadows.rayTracedShadows / PostFX.RayTracedShadows) ---
    // A scene BLAS/TLAS (Dx12SceneAS) + an RT pass (DxrShadows.hlsl) that traces one shadow ray per pixel
    // toward the sun → a full-res R8 mask the deferred lighting multiplies into the sun term (UseRtShadows).
    // Built lazily on first use; falls back to the cascaded CSM when DXR is unavailable. Hard shadows are
    // deterministic (no denoise). The scene AS, ID3D12Device5 facet, DXR-availability probe, and
    // per-instance bindless geo SRVs live in the shared Dx12DxrShared holder.
    Dx12DxrShared dxr;

    ID3D12RootSignature rtShadowRootSig; // CBV(b0) + table{SRV t0 TLAS, t1 depth, t2 normal; UAV u0 mask}
    ID3D12StateObject rtShadowPso;
    ID3D12Resource rtShadowSbt, rtShadowCb;
    unsafe byte* rtShadowCbMapped;
    Dx12OffscreenTarget rtShadowMask; // full-res R8 (1 lit / 0 shadowed), UAV + SRV
    Dx12DescriptorHeap rtShadowHeap; // 4 descriptors (rebuilt per frame)
    bool rtShadowBuilt;
    bool rtShadowsThisFrame;
    const int RtSbtSlot = 64; // shader-table record alignment

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct RtShadowConstants
    {
        public Matrix4x4 InvViewProj;
        public Vector3 SunDir;
        public float NormalBias;
    }

    // --- DXR ray-traced reflections (Reflection volume SSR-vs-RT dropdown: PostFX.ReflectionMode) ---
    // Reuses Dx12SceneAS + the SSR reflection target (ssrTarget) + the SSR combine (ssrCombinePso): the RT
    // pass writes (reflected color, strength) into ssrTarget, then the existing depth-aware Fresnel combine
    // mixes it into the scene. DxrReflections.hlsl shades misses as the sky/IBL cube and hits with
    // direct light plus IBL ambient. Mirror rays are deterministic.
    // RT reflections moved VERBATIM to Resources/Dx12ReflectionsPass.cs (chunk 10): the rtRefl rootsig/PSO/SBT/
    // CBs + the RtReflTableBase bindless-tail constant + the RtReflConstants struct + EnsureRtReflections/
    // DrawRtReflections, all alongside SSR as ONE RT-vs-SSR mode-branch pass. The shared
    // sceneAS/device5/rtGeometry live in the Dx12DxrShared holder (`dxr`, threaded via ctx.Dxr).

    // BALLISTIC_DETERMINISTIC=1 — byte-deterministic, frame-INDEPENDENT captures (the documented "frame 60 ==
    // frame 240" contract; `bal render`/`bal gbuffer` set it). On DX12 this had NO consumer (it was a GL-only
    // implementation), so TAA's per-frame Halton jitter left captures frame-count-dependent. Wired here:
    // kill TAA jitter for deterministic captures. Exposure is already pinned by BALLISTIC_DX12_EXPOSURE.
    bool? deterministicOn;

    bool DeterministicCapture =>
        deterministicOn ??= Environment.GetEnvironmentVariable("BALLISTIC_DETERMINISTIC") == "1";

    // Transparent forward pass (back-to-front alpha-blended Material.Transparent submeshes, full forward PBR)
    // is owned by Dx12TransparentsPass (chunk 8 — Event=Transparents 450; BuildTransparentPass/DrawTransparents
    // + the TransparentConstants struct + the AabbInFrustum/ToNumerics/BindSrvInto helpers moved into it).
    Dx12TransparentsPass transparentsPass;

    // Camera projection near/far — shared by the projection build AND the froxel log-Z grid (must match).
    const float CameraNear = 0.1f, CameraFar = 1000f;

    // Final composite (HDR scene → exposure → ACES → +bloom → sRGB → LDR) — CONVERTED to a pass-graph
    // IRenderPass (chunk 7): Dx12CompositePass owns the composite rootsig/PSO/CB/heap AND its private sub-steps
    // (auto-exposure metering + bloom: their rootsigs/PSOs/targets/CBs/heaps moved into the pass too — splitting
    // them out would be a restructure, not a move; trap 3). Runs at the Composite event via the graph, after the
    // (still-inline) TAA/FSR block, reading ctx.SceneColor.
    Dx12CompositePass compositePass;

    // GTAO (ground-truth AO, Jimenez 2016): from the G-buffer (depth + normal + albedo) → blurred AO target.
    // Runs at AfterGBuffer (event 200), BEFORE deferred lighting, which samples it (ctx.AoResult) and multiplies
    // it into the IBL ambient term only — the physically-correct, ambient-only layer (the old HBAO/Dx12SsaoPass
    // ran post-deferred and post-multiplied the whole HDR colour). All params come from the AmbientOcclusion volume.
    Dx12GtaoPass gtaoPass;
    Dx12RtaoPass rtaoPass;

    // chunk 9: Deferred lighting (event 300 — OpaqueLighting). Owns the deferred rootsig/PSO/CB/13-SRV heap;
    // reads ctx light/IBL/cluster/rtShadow state + draws the full-screen lit HDR into `target` (head transition
    // gbuffer.ToShaderResource — R2). Was BuildDeferredLighting / DrawDeferredLighting (+ the LightConstants struct).
    Dx12DeferredLightingPass deferredPass;

    // chunk 5: AerialPerspective (event 400) + Fog (event 550), converted leaf-post passes (was the inline
    // DrawAerialPerspective / DrawFog). Both blend in place into `target` — no cross-pass output getter.
    Dx12AerialPerspectivePass apPass;
    Dx12FogPass fogPass;

    // Sky (background): the asset cubemap Skybox + the procedural atmosphere are both owned by Dx12SkyPass
    // (chunk 8 — Event=Sky 350; BuildSkybox/BuildProcSky/DrawSkybox/DrawProcSky + the SkyboxConstants/
    // ProcSkyConstants structs moved into it). The pass draws the sky into the HDR color at the far plane.
    Dx12SkyPass skyPass;

    // Reflections (event 600): the single RT-vs-SSR mode-branch pass. It owns the SSR rootsig/PSOs/targets
    // and the RT-reflection pipeline.
    Dx12ReflectionsPass reflectionsPass;

    // Per-draw constant buffer ring: one upload heap sub-allocated in 256-byte slots, one slot per draw.
    ID3D12Resource cbRing;
    int cbSlotSize;
    int cbSlotCount;
    unsafe byte* cbMapped;

    // Shader-visible SRV heap: per draw we copy the material's diffuse SRV into the next slot and point
    // the root descriptor table at it. Reset each frame.
    Dx12DescriptorHeap srvVisible;

    // Matches StandardOpaque.hlsl's cbuffer DrawConstants byte-for-byte (16-byte-aligned rows).
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct DrawConstants
    {
        public Matrix4x4 Mvp;
        public Matrix4x4 Model;
        public Vector3 LightDir;
        public float Exposure;
        public Vector3 LightColor;
        public float Metallic;
        public Vector3 Ambient;
        public float Roughness;
        public Vector3 CameraPos;
        public float SpecularReflectance;
        public Vector4 BaseColorFactor;
        public Vector3 EmissiveFactor;
        public float HasEmissive;
        public float NormalStrength, NormalFlipY, HasMetallicMap, HasRoughnessMap;
        public float PackedOrm, Cutout, UseIBL, PrefilterMaxMip;
    }

    // The 6 material maps in HLSL register(t0..t5) order.
    const int MaterialSrvCount = 6;

    // IBL: baker (env→irradiance/prefilter/BRDF) + a per-frame 3-SRV shader-visible table (t6..t8).
    Dx12IblBaker ibl;
    Dx12DescriptorHeap iblSrvVisible; // 3 contiguous SRVs copied per frame
    bool iblActiveThisFrame;

    // Sky-atmosphere LUTs (Hillaire 2020), SEPARATE from the IBL baker: v1 = Transmittance LUT.
    Dx12SkyLuts skyLuts;

    // Sun cascaded shadows. MaxCascades is the allocated array depth (the shadow-map texture array + the CB
    // slot budget + the FrameConstants cascade slots are all sized for this); the Shadows volume's cascadeCount
    // selects how many of these are actually fit/rendered/sampled (1..MaxCascades) without reallocation. The
    // shadow-map RESOLUTION is volume-driven too, but a resolution change recreates the texture (BuildShadows
    // sizes the array), so it's applied lazily in RenderShadows when PostFX.ShadowResolution differs.
    const int MaxCascades = 4;
    int shadowMapSize = 2048;     // current allocated per-cascade resolution (PostFX.ShadowResolution)
    int activeCascadeCount = 4;   // cascades fit/rendered this frame (PostFX.ShadowCascadeCount, clamped)
    Dx12ShadowMap shadowMap;
    ID3D12RootSignature shadowRootSig; // ShadowConstants CBV (b0)
    ID3D12PipelineState shadowPso;
    ID3D12Resource shadowCb; // per (cascade,submesh) LightMvp slots, upload heap
    unsafe byte* shadowCbMapped;
    int shadowCbSlotSize, shadowCbSlotCount;
    readonly Matrix4x4[] cascadeMatrices = new Matrix4x4[MaxCascades];
    readonly float[] cascadeDepthRanges = new float[MaxCascades];
    bool shadowsThisFrame;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct ShadowConstants
    {
        public Matrix4x4 LightMvp;
    }

    // Volumetric fog + aerial perspective moved to Dx12FogPass / Dx12AerialPerspectivePass (chunk 5). Their
    // root sigs / PSOs / CBs / heaps + the FogConstants/ApConstants structs now live inside those pass classes.

    // Per-frame constants (b1) shared by every opaque draw: the cascade matrices + shadow params. The tail
    // block (Filtering..ContactThickness) is volume-driven by the Shadows VolumeComponent → PostFX → here →
    // DeferredLighting.hlsl (must stay 16-byte aligned; kept in two float4 rows). HLSL layout must match.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    unsafe struct FrameConstants
    {
        public Matrix4x4 Cascade0, Cascade1, Cascade2, Cascade3;
        public Vector4 CascadeBias; // per-cascade depth-compare bias
        public float CascadeCountF;
        public float ShadowsEnabled;
        public float ShadowMapTexel;
        public float CascadeBlend;
        // Shadows volume tail (b1): row 1 = filtering mode / PCSS softness / contact toggle+length.
        public float ShadowFiltering;     // 0 = hard, 1 = soft PCF, 2 = PCSS
        public float ShadowSoftness;      // PCSS penumbra scale
        public float ContactShadowsOn;    // >0.5 = march screen-space contact shadow
        public float ContactShadowLength; // world metres marched
        // row 2 = contact march tuning + pad.
        public float ContactShadowSteps;
        public float ContactShadowThickness;
        public float FramePad0, FramePad1;
    }

    ID3D12Resource frameCb;
    unsafe byte* frameCbMapped;

    public DX12HDRenderer(Dx12Device device)
    {
        dev = device;
    }

    // Editor viewport display: the final LDR composite (`ldr`) is mirrored into the shared shader-visible
    // UI heap (Dx12Backend.UiHeap) so the editor's ImGui pass can sample it via ImGui.Image. SceneColorHandle/
    // GameColorHandle return the cached GPU descriptor ptr (a trivial field read — no per-frame descriptor
    // churn, which is banned in the hot path). Single `ldr` today, so Scene and Game alias the same texture
    // (the editor renders one ActiveTarget per frame). RenderHandle.None until the first allocation.
    int ldrUiSlot = -1;
    nint ldrUiHandle;
    public override RenderHandle SceneColorHandle => new(ldrUiHandle);
    public override RenderHandle GameColorHandle => new(ldrUiHandle);

    // The final composited LDR color resource (R8G8B8A8_UNORM). The windowed DX12 player host blits this
    // straight into the swapchain backbuffer (PresentToScreen path); the editor samples it via the UI heap.
    public ID3D12Resource DisplayResource => ldr?.RenderTarget;
    public int DisplayWidth => outputW;
    public int DisplayHeight => outputH;

    // Spatial-query substrate (the AI agent's "eyes"): a GpuSceneQuery over the scene TLAS. Created on
    // demand (not per-frame) — the headless `bal query` path + the editor live-query surface use it. Owns
    // its OWN Dx12SceneAS so queries work with RT render effects off. See GpuSceneQuery / the proposal doc.
    public GpuSceneQuery CreateSceneQuery() => new GpuSceneQuery(dev);

    // DX12 textures are top-down → the editor must NOT flip V (unlike GL's bottom-up textures).
    public override bool DisplayTextureTopDown => true;

    // Mirror the LDR composite's SRV into the shared UI heap at a STABLE slot (re-pointed on resize so the
    // ImGui handle stays constant). Headless registers too — harmless; the handle is just never sampled
    // without an editor present. Requires `ldr` to have been created colorReadable (so it owns an SRV).
    void RegisterLdrUi()
    {
        if (Dx12Backend.UiHeap == null) return;
        if (ldrUiSlot < 0) ldrUiSlot = Dx12Backend.UiHeap.Allocate();
        Dx12Backend.RegisterUiAt(ldrUiSlot, ldr.ColorSrvCpu);
        ldrUiHandle = (nint)Dx12Backend.UiHeap.Gpu(ldrUiSlot).Ptr;
    }

    public override void ResizeSceneTarget(int width, int height) => Resize(width, height);
    public override void ResizeGameTarget(int width, int height) => Resize(width, height);

    void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        if (target != null && width == outputW && height == outputH) return;
        outputW = width;
        outputH = height;
        // Reset to native (internal == output); BeginRender's EnsureUpscaleTargets re-derives the internal
        // render resolution from the volume's UpscaleMode and reallocates if FSR wants a smaller render res.
        fsrActive = false;
        currentUpscaleMode = UpscaleMode.Off;
        fsr?.Dispose();
        fsr = null;
        AllocateResolutionTargets(width, height);
    }

    // (Re)allocate every resolution-dependent target. internalW/H = the render resolution (scene + all post
    // passes); ldr + fsrOutput are at the output resolution. Called on resize and on an FSR mode change.
    void AllocateResolutionTargets(int internalW, int internalH)
    {
        // GPU MUST be idle before freeing the old targets: a resize (e.g. dragging the editor from the 4K
        // to the 1080p monitor) reallocates these while the previous frame's commands may still read them.
        // Disposing under an active GPU read is a use-after-free → TDR → DXGI_ERROR_DEVICE_REMOVED. Flush
        // also drains in-flight worker uploads (see Dx12Device.Flush). Realloc is rare (resize / FSR mode).
        dev.Flush();
        targetW = internalW;
        targetH = internalH;
        target?.Dispose();
        ldr?.Dispose();
        gbuffer?.Dispose();
        // The HDR scene target no longer owns depth — the G-buffer owns the scene depth (deferred path).
        target = new Dx12OffscreenTarget(dev, internalW, internalH, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
        ldr = new Dx12OffscreenTarget(dev, outputW, outputH, colorReadable: true); // LDR composite output (display res)
        RegisterLdrUi();
        // Editor display: the editor's ImGui pass samples ldr (SceneColorHandle) EVERY frame, including
        // before the scene first composites (long async import). Leave it sample-ready (PixelShaderResource)
        // so it's never sampled as an SRV while in RenderTarget state — that undefined access hangs the GPU
        // over many frames (DXGI_ERROR_DEVICE_HUNG). Composite transitions PSR->RT->PSR per frame thereafter.
        // Unconditional (not gated on PresentToScreen): the editor sets PresentToScreen=false AFTER Initialize,
        // and it's harmless headless (SaveBmp transitions from any state).
        ldr.ColorToShaderResource();
        gbuffer = new Dx12GBuffer(dev, internalW, internalH);
        motionPrevValid = false; // prev view*proj is stale after a realloc
        // Bloom (Dx12CompositePass), TAA (Dx12TaaPass), GTAO, and reflections now reallocate via
        // graph.Resize at the tail of this method — see the graph?.Resize call below. Their original AllocXxx
        // slots are byte-neutral because each allocator reads only the passed size (R5).
        if (rtShadowMask != null) AllocRtShadowMask();
        AllocFsrOutput();
        // Fan the resize out to any pass that owns resolution-dependent targets. No-op while the graph is
        // empty (scaffold). Registration order matches the AllocXxx sequence above once passes populate it (R5).
        graph?.Resize(internalW, internalH);
        // PHASE-3 (chunk 20): the feature blitter's scratch HDR copy follows the render res (it also self-sizes to
        // the live SceneColor per-blit, so this is just to avoid a first-blit reallocation).
        featureBlitter?.Resize(internalW, internalH);
    }

    void AllocFsrOutput()
    {
        fsrOutput?.Dispose();
        // Output-resolution HDR target FSR writes via UAV and the composite reads via SRV.
        fsrOutput = new Dx12OffscreenTarget(dev, outputW, outputH, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: true);
    }

    // Map the volume UpscaleMode to an FSR quality id.
    static uint FsrQuality(UpscaleMode m) => m switch
    {
        UpscaleMode.NativeAA => FfxApi.QualityNativeAA,
        UpscaleMode.Quality => FfxApi.QualityQuality,
        UpscaleMode.Balanced => FfxApi.QualityBalanced,
        UpscaleMode.Performance => FfxApi.QualityPerformance,
        UpscaleMode.UltraPerformance => FfxApi.QualityUltraPerformance,
        _ => FfxApi.QualityQuality,
    };

    // Ensure the internal render resolution + FSR context match the requested upscale mode. Reallocates the
    // internal-res targets (and recreates the FSR context) only when the mode actually changes. If the FSR
    // DLLs can't load it latches off and renders native (graceful degrade on a clean checkout).
    void EnsureUpscaleTargets(UpscaleMode mode)
    {
        bool wantFsr = mode != UpscaleMode.Off && !fsrUnavailable;
        int wantIW = outputW, wantIH = outputH;
        if (wantFsr)
        {
            try
            {
                (wantIW, wantIH) = Dx12FsrUpscaler.RenderResolutionFor(outputW, outputH, FsrQuality(mode));
            }
            catch (Exception e)
            {
                Console.WriteLine($"[FSR] unavailable, rendering native: {e.Message}");
                fsrUnavailable = true;
                wantFsr = false;
                wantIW = outputW;
                wantIH = outputH;
            }
        }

        if (target != null && wantIW == targetW && wantIH == targetH && fsrActive == wantFsr)
        {
            currentUpscaleMode = mode;
            return; // nothing to reallocate
        }

        AllocateResolutionTargets(wantIW, wantIH);
        if (wantFsr)
        {
            try
            {
                fsr?.Dispose();
                fsr = new Dx12FsrUpscaler(dev, wantIW, wantIH, outputW, outputH);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[FSR] context create failed, rendering native: {e.Message}");
                fsrUnavailable = true;
                wantFsr = false;
            }
        }

        fsrActive = wantFsr;
        currentUpscaleMode = mode;
    }

    public override unsafe void Initialize()
    {
        // Clustered-deferred: geometry → G-buffer (owns scene depth) → deferred lighting → HDR `target`
        // (color only) → sky/fog/post → composite into `ldr` (R8). `target` no longer owns depth.
        // At init internal == output (FSR off); EnsureUpscaleTargets adjusts once a volume requests FSR.
        outputW = targetW;
        outputH = targetH;
        target = new Dx12OffscreenTarget(dev, targetW, targetH, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
        ldr = new Dx12OffscreenTarget(dev, targetW, targetH, colorReadable: true);
        RegisterLdrUi();
        ldr.ColorToShaderResource(); // sample-safe before first composite (see AllocateResolutionTargets)
        gbuffer = new Dx12GBuffer(dev, targetW, targetH);
        BuildRootSignature();
        BuildPipeline();
        BuildGeometryPass();

        cbSlotSize = (System.Runtime.InteropServices.Marshal.SizeOf<DrawConstants>() + 255) & ~255;
        cbSlotCount = 8192; // submesh draws per frame ceiling (SunTemple ~hundreds)
        cbRing = dev.Device.CreateCommittedResource(
            HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(cbSlotSize * cbSlotCount)), ResourceStates.GenericRead);
        cbMapped = cbRing.Map<byte>(0);

        // 6 SRVs per draw (the material table) — size the ring for the worst-case draw count.
        srvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            cbSlotCount * MaterialSrvCount, shaderVisible: true, framesInFlight: dev.FramesInFlight);

        BuildSkinnedGeometryPass();

        // Sky (skybox + procedural atmosphere) now built inside Dx12SkyPass's ctor (chunk 8), constructed
        // below at pass-graph assembly. Was BuildSkybox() + BuildProcSky().

        ibl = new Dx12IblBaker(dev);
        skyLuts = new Dx12SkyLuts(dev);
        // 3 IBL SRVs (irradiance/prefilter/BRDF) copied contiguously per frame into a shader-visible heap.
        iblSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 3, shaderVisible: true,
            framesInFlight: dev.FramesInFlight);

        BuildShadows();

        int frameCbSize = (System.Runtime.InteropServices.Marshal.SizeOf<FrameConstants>() + 255) & ~255;
        frameCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)frameCbSize), ResourceStates.GenericRead);
        frameCbMapped = frameCb.Map<byte>(0);

        // Deferred lighting now built inside Dx12DeferredLightingPass's ctor (chunk 9), constructed below at
        // pass-graph assembly. Was BuildDeferredLighting(). clusteredLights (the CPU froxel gather feeds it)
        // stays orchestrator-owned — built here, the deferred pass reads it via ctx.
        clusteredLights = new Dx12ClusteredLights(dev);
        // Transparents now built inside Dx12TransparentsPass's ctor (chunk 8), constructed below at pass-graph
        // assembly. Was BuildTransparentPass(). Fog + AerialPerspective likewise inside their pass ctors
        // (Dx12FogPass / Dx12AerialPerspectivePass, chunk 5). Was BuildFog() + BuildAerialPerspective().
        // Reflections now built inside Dx12ReflectionsPass's ctor, constructed below at pass-graph assembly.
        // TAA now built inside Dx12TaaPass's ctor (chunk 7); composite (+ its private bloom + auto-exposure
        // sub-steps) inside Dx12CompositePass's ctor — both constructed below at pass-graph assembly. Was
        // BuildTaa() + BuildComposite().

        // GPU-driven geometry path (compute cull + ExecuteIndirect + bindless) for whole-mesh renderers.
        // DEFAULT ON (byte-identical to the CPU path, verified on Bistro + SunTemple); BALLISTIC_DX12_GPUDRIVEN=0
        // falls back to the per-submesh CPU draw loop. Mirrors the GL BALLISTIC_GPUDRIVEN convention.
        gpuDrivenOn = Environment.GetEnvironmentVariable("BALLISTIC_DX12_GPUDRIVEN") != "0";
        // Hi-Z occlusion cull: DEFAULT ON (verified byte-identical + culls 894->224 submeshes on SunTemple);
        // BALLISTIC_DX12_GPUDRIVEN_HIZ=0 disables it. Mirrors the GL BALLISTIC_GPUDRIVEN_HIZ convention.
        hizWanted = gpuDrivenOn && Environment.GetEnvironmentVariable("BALLISTIC_DX12_GPUDRIVEN_HIZ") != "0";
        // Cascade caching: DEFAULT ON (BALLISTIC_DX12_SHADOW_CACHE=0 disables).
        shadowCacheOn = Environment.GetEnvironmentVariable("BALLISTIC_DX12_SHADOW_CACHE") != "0";
        // Resolve the per-feature doors ONCE (the BALLISTIC_DX12_MINIMAL switch + cached env reads).
        doors = Dx12RenderDoors.Resolve();
        if (doors.Minimal)
            Console.WriteLine("[DX12] BARE-MINIMUM render: G-buffer + deferred (sun/punctual) + composite only. " +
                              "Re-enable per pass with BALLISTIC_DX12_{SHADOWS,SKY,IBL,SSAO,BLOOM,AP,VOLUMES}=1 / BALLISTIC_FX_VOLUMETRIC=1.");
        gpuDriven = new Dx12GpuDrivenRenderer(dev);
        // Drop every per-scene CACHED cull/draw state on a scene swap. These caches are keyed by cheap change
        // stamps (not scene identity): the GPU-driven material table (renderer+submesh count), the Hi-Z prime,
        // the shadow-cascade cache. Two scenes with matching stamps would keep the first scene's table/cull
        // state — the second scene then renders wrong / culls everything. SceneManager raises this AFTER it
        // empties the render sets (StopPlay / LoadScenePath / editor ApplyNow all route through it).
        SceneManager.RenderSetsCleared += OnRenderSetsCleared;

        AllocFsrOutput(); // output-res UAV target for FSR (allocated even when off — cheap, simplifies resize)

        // Shared DXR substrate holder (sceneAS/device5/dxr-availability/rtGeometry), lazily filled on first RT use.
        // Created BEFORE the graph so RT sun shadows and reflections share it.
        dxr = new Dx12DxrShared(dev);

        // Phase-1 pass-graph: build the executor and register the converted passes. Wired with the renderer's
        // TimePass so converted passes get GPU timings. Register in the original AllocateResolutionTargets
        // AllocXxx order so graph.Resize fans out in a stable sequence (R5). Build() once for stable order (R1).
        graph = new Dx12RenderGraph(TimePass);
        // chunk 9: Deferred lighting (event 300 — the OpaqueLighting slot, EARLIEST of all converted passes:
        // 300 < Sky 350 < AP 400 < Transparents 450 < Fog 550 < PostProcess 650 < Composite 700). Owns no
        // resolution targets (full-screen draw into `target`), so its Resize is a no-op → registration order is
        // R5-neutral; registered first for hygiene (matches its earliest event). Was BuildDeferredLighting +
        // the inline gbuffer.ToShaderResource() + DrawDeferredLighting call.
        deferredPass = new Dx12DeferredLightingPass(dev);
        graph.Add(deferredPass);
        gtaoPass = new Dx12GtaoPass(dev, targetW, targetH); // GTAO at AfterGBuffer (200), feeds the deferred ambient (was Dx12SsaoPass)
        graph.Add(gtaoPass);
        // RT sky-occlusion (BeforeOpaqueLighting 250, opt-in BALLISTIC_DX12_RTAO=1): reads GTAO's AO, multiplies it
        // by real sky-visibility (DXR hemisphere rays), copies the result back — so the deferred IBL/flat ambient
        // is gated by how open each pixel is (a closed interior stops glowing from ambient it can't receive).
        rtaoPass = new Dx12RtaoPass(dev, gtaoPass);
        graph.Add(rtaoPass);
        // chunk 8: Sky (event 350 — skybox + procedural atmosphere). Resolution-independent (no Resize body)
        // so registration order doesn't touch R5; the event sort places it FIRST of the converted passes (350
        // < AP 400 < Fog 550 < PostProcess 650). Registered before apPass for same-event-tiebreak hygiene (R1),
        // though Sky 350 != AP 400 so it's not load-bearing. Was BuildSkybox + BuildProcSky.
        skyPass = new Dx12SkyPass(dev);
        graph.Add(skyPass);
        // chunk 5: AerialPerspective (event 400) + Fog (event 550). Both resolution-independent (no Resize
        // body) so registration order doesn't touch R5; the event sort places them before SSAO (650) — the
        // same relative order as today's inline frame (AP before transparents, fog before SSR; both
        // before SSAO). Was BuildAerialPerspective / BuildFog.
        apPass = new Dx12AerialPerspectivePass(dev);
        fogPass = new Dx12FogPass(dev);
        graph.Add(apPass);
        graph.Add(fogPass);
        // chunk 8: Transparents (event 450 — after Sky 350 + AP 400, before Fog/SSR). Resolution-independent
        // (no Resize body), so registration order is R5-neutral; the event sort places it at 450. Was
        // BuildTransparentPass + the inline DrawTransparents call.
        transparentsPass = new Dx12TransparentsPass(dev);
        graph.Add(transparentsPass);
        // Reflections (event 600) owns its resolution targets and branches between SSR and RT reflections.
        reflectionsPass = new Dx12ReflectionsPass(dev, targetW, targetH);
        graph.Add(reflectionsPass);
        // chunk 7: TAA (event 650, registered AFTER SSAO so SSAO runs first within PostProcess — same-event ties
        // break on registration order, R1). Owns the ping-pong history; its Resize fans out after SSAO's (taa was
        // 5th in the old AllocXxx order — byte-neutral, the allocator reads only the size, R5). Was BuildTaa.
        taaPass = new Dx12TaaPass(dev, targetW, targetH);
        graph.Add(taaPass);
        // chunk 7: FSR (event 650, registered after TAA — mutually exclusive: FsrPass.Enabled=FsrActive,
        // TaaPass.Enabled=!FsrActive, so exactly one runs). Owns no resources (fsr/fsrOutput orchestrator-owned),
        // so no Resize. Sets ctx.SceneColor = fsrOutput. Was RunFsr.
        fsrPass = new Dx12FsrPass(dev);
        graph.Add(fsrPass);
        // chunk 7: Composite (event 700, after SSAO/TAA at PostProcess=650). Owns bloomA/B (the half-res
        // ping-pong); its Resize fans out LAST in registration order. The original AllocBloomTargets ran FIRST in
        // AllocateResolutionTargets, but the bloom allocator reads only the passed size (no cross-pass dependency)
        // so moving it to last is byte-neutral (R5). Was BuildComposite (→ BuildLumAverage → BuildBloom).
        compositePass = new Dx12CompositePass(dev, targetW, targetH);
        graph.Add(compositePass);
        // chunk 12 (phase 2 V1): a cull-path coverage pass. AllowCulling is default-OFF per pass, so without one
        // pass that opts in, the culler (opaque-edge rule + iterate-to-fixpoint + non-imported-write decision)
        // would ship UNTESTED (plan §V1, R-NEW-8). Dx12CullProbePass writes one non-imported scratch nobody reads
        // → the compiler culls it every graph frame, exercising the culler. It NEVER records (culled on the graph
        // path; Enabled=false on the list path) → byte-neutral in both. Event=BeforeShadows(0) → ordering-inert.
        graph.Add(new Dx12CullProbePass());
        // PHASE-3 (chunk 20): mark the BUILT-IN boundary — everything Add'd above is core. Authored render-feature
        // adapters are appended AFTER this by the bridge (Dx12RenderFeatureBridge → graph.SetFeaturePasses) when a
        // scene has a RenderFeatures SceneBehaviour. With no features the boundary == registered.Count, so the
        // graph is exactly the built-in set → byte-identical to the feature-free golden path (pixel-neutral default).
        graph.MarkCoreBoundary();
        graph.Build();
        // chunk 12: COMPILE the V1 dependency graph (DAG → cull → topo order). Pure CPU bookkeeping, no GPU work
        // — V1 maps handles 1:1 to existing concrete targets (no pooling). The compiled order is what
        // ExecuteGraph runs when graphPath is on. Compile here so any Declare/cycle error surfaces at init, not
        // mid-frame; ExecuteGraph also lazily compiles if needed.
        graph.Compile();
        // Resolve the phase-2 V1 door once at init (kills per-frame env churn). When set, BeginRender calls
        // graph.ExecuteGraph(ctx) (compiled order) instead of graph.Execute(ctx) (event sort).
        graphPath = Environment.GetEnvironmentVariable("BALLISTIC_DX12_GRAPH") == "1";
        if (graphPath)
        {
            Console.Error.WriteLine(
                "[DX12] Render graph (phase-2 V1): COMPILED ORDER active (BALLISTIC_DX12_GRAPH=1).");
            Console.Error.WriteLine(graph.LastCompileReport);
        }

        // PHASE 2 V3 (chunk 14): auto-derived boundary barriers. Gated behind BALLISTIC_DX12_GRAPH_BARRIERS=1
        // (requires GRAPH=1 — it runs in ExecuteGraph). When set, the graph DERIVES each migrated pass's head
        // transition from its declared Usages and emits it before Record; the migrated pass skips its manual head
        // transition (ctx.BarriersDerived). Default off → migrated passes emit their manual head transitions,
        // byte-identical to V1/V2. Compile already built + plan-level-validated the deriver (CompareToManual);
        // print the comparison so the manual-vs-derived sets are auditable.
        barriersPath = graphPath && Environment.GetEnvironmentVariable("BALLISTIC_DX12_GRAPH_BARRIERS") == "1";
        graph.SetBarriersDerived(barriersPath);
        if (barriersPath)
        {
            Console.Error.WriteLine(
                "[DX12] Render graph (phase-2 V3): AUTO-DERIVED BARRIERS active (BALLISTIC_DX12_GRAPH_BARRIERS=1).");
            Console.Error.WriteLine(graph.LastDeriverReport);
        }

        // === PHASE 2 V2 (chunk 13): the TRANSIENT RENDER-TARGET POOL + lifetime ALIASING. Gated behind
        // BALLISTIC_DX12_GRAPH_ALIAS=1 (requires GRAPH=1 — it reads the COMPILED order for lifetimes). Default off →
        // every pooled pass's AllocOrPool falls through to a committed target, byte-identical to V1/phase-1. ===
        // Lifetime model (the key V2 insight): every pooled scratch target is PASS-PRIVATE — born and consumed
        // ENTIRELY within ONE pass's Record (ssgiTarget→…→ssgiScene inside the GI Record; bloomA/B inside Composite;
        // etc.), never crossing a pass boundary. So a target's lifetime is exactly its OWNING PASS's compiled-order
        // position. Targets in the SAME pass coexist (must NOT alias → distinct regions); targets in DIFFERENT
        // passes have disjoint lifetimes (CAN alias). Registering each with first==last==passOrder makes the greedy
        // interval-coloring produce exactly that (same-pass overlap → separate region; cross-pass disjoint → share).
        aliasPath = graphPath && Environment.GetEnvironmentVariable("BALLISTIC_DX12_GRAPH_ALIAS") == "1";
        if (aliasPath)
        {
            rtPool = new Dx12RenderTargetPool(dev);
            int hw = Math.Max(1, targetW / 2), hh = Math.Max(1, targetH / 2);
            var Hdr = Dx12OffscreenTarget.HdrFormat;
            int refl = graph.OrderIndexOf("Reflections");
            int gi = graph.OrderIndexOf("GI"), comp = graph.OrderIndexOf("Composite");
            // POOLED (audit-passed full-overwrite-before-read transients). Format/size/uav MUST match the pass's
            // actual AllocOrPool call (the pool asserts this on Acquire). History (ssgiHistory/taaHistory/lumTarget)
            // is NOT registered → those passes' AllocOrPool calls fall through to committed (imported, never aliased).
            //
            // NOTE: GTAO's gtaoA/gtaoB are deliberately NOT pooled — their size depends on the AmbientOcclusion
            // volume's Resolution dropdown (Full/Half/Quarter), which can change at runtime, so a fixed-size pool
            // registration would break the pool's size-match assert. They fall through to committed targets (the
            // pool is a perf optimisation, not a correctness requirement; committed is always valid).
            //
            // LIFETIME = [firstWritePass, lastReadPass] in compiled-graph order. The remaining pooled scratch is all
            // PASS-PRIVATE (born + consumed inside ONE pass's Record → first==last==that pass).
            rtPool.Register("ssrTarget", hw, hh, Hdr, true, refl, refl);
            rtPool.Register("ssrScene", targetW, targetH, Hdr, false, refl, refl);
            rtPool.Register("ssgiTarget", hw, hh, Hdr, true, gi, gi);
            rtPool.Register("ssgiDenoised", hw, hh, Hdr, true, gi, gi);
            rtPool.Register("ssgiScene", targetW, targetH, Hdr, false, gi, gi);
            rtPool.Register("bloomA", hw, hh, Hdr, false, comp, comp);
            rtPool.Register("bloomB", hw, hh, Hdr, false, comp, comp);
            rtPool.BuildPlan();
            // V2 SOUNDNESS GATE: assert no two aliased logicals have overlapping lifetimes (the invariant the whole
            // pixel-neutral guarantee rests on). A violation throws at init rather than corrupting memory mid-frame.
            string overlap = rtPool.AuditNoOverlap();
            if (overlap != null) throw new InvalidOperationException("[DX12 V2] alias plan UNSOUND — " + overlap);
            Dx12RenderTargetPool.Active = rtPool;
            // Re-Resize so every pooled pass RE-ACQUIRES its target as a PLACED resource (its ctor allocated
            // committed targets before the pool existed; AllocOrPool now hands back placed ones, disposing the
            // committed pre-pool allocation). Same registration-order fan-out as the normal resize path (R5).
            graph.Resize(targetW, targetH);
            Console.Error.WriteLine(
                "[DX12] Render graph (phase-2 V2): TRANSIENT ALIASING active (BALLISTIC_DX12_GRAPH_ALIAS=1).");
            Console.Error.WriteLine(rtPool.PlanReport);
        }

        // PHASE-3 (chunk 20): the render-FEATURE bridge — the engine→backend seam for authored RenderFeatures (the
        // mirror of the volume bridge). The blitter owns the proof feature's full-screen GPU work; the recorder is
        // the backend-agnostic verb surface a feature.Record drives; the bridge gathers active features per frame
        // and, only when the set changes, builds Dx12FeaturePassAdapters and graph.SetFeaturePasses them. Inert for
        // feature-free scenes (Gather()==0 → SameAsLast → no-op), so the golden scenes are byte-identical.
        featureBlitter = new Dx12FeatureBlitter(dev, targetW, targetH);
        featureRecorder = new Dx12FeaturePassRecorder(featureBlitter);
        featureBridge = new Dx12RenderFeatureBridge(graph, featureRecorder);
    }

    Dx12GpuDrivenRenderer gpuDriven;
    bool gpuDrivenOn;

    // Scene-swap cache reset (subscribed to SceneManager.RenderSetsCleared in the ctor). Forces the GPU-driven
    // material table to rebuild for the new scene (its count-stamp could coincide with the old scene's), drops
    // the Hi-Z prime (the pyramid holds the OLD scene's depth — a near-identical new camera would otherwise cull
    // the whole new scene behind stale occluders), and invalidates the shadow-cascade cache (a matching caster
    // stamp would reuse the old scene's shadow map). hizLastCamPos/lastCasterStamp are stamps, not state — they
    // get overwritten before they're read again, so they need no reset here.
    void OnRenderSetsCleared() {
        gpuDriven.Invalidate();
        hizPrimed = false;
        shadowMapEverRendered = false;
    }

    bool hizWanted;

    // Cached per-feature on/off doors (resolved once at init from the BALLISTIC_DX12_*/_FX_* env vars).
    // Implements the BALLISTIC_DX12_MINIMAL "bare minimum" diagnostic switch + kills the per-frame env churn.
    Dx12RenderDoors doors;

    // Live editor control of the door-gated passes (the "Render Pass Toggles" window). Reading is free; a
    // write reassigns the whole struct, which BeginRender copies into the next frame's ctx → the toggle takes
    // effect next frame with zero per-frame cost (mirrors how PostFX is a renderer-owned live object). The
    // PostFX-gated passes (Fog/SSR/GI) already toggle live via the Volume framework — these are the rest.
    public Dx12RenderDoors Doors {
        get => doors;
        set => doors = value;
    }
    public void SetDoor(string door, bool value) => doors = doors.With(door, value);
    Vector3 hizLastCamPos;
    bool hizPrimed; // false until we have a valid previous-frame depth (first frame / after a big jump)
    readonly System.Collections.Generic.List<IStaticMeshRenderer> wholeMeshRenderers = new();

    // The pluggable pass list (phase 1 — the URP pre-RenderGraph model: a stably-event-ordered IRenderPass
    // list). PHASE 1 COMPLETE: every non-core pass is registered here and run by graph.Execute(ctx) (the event
    // sort). PHASE 2 V1 (chunk 12): the same passes now also DECLARE reads/writes (IRenderPass.Declare), so
    // graph.Compile() can build a dependency DAG, cull, and derive a topo order; graph.ExecuteGraph(ctx) runs
    // THAT order. Built once (stable order, R1). Resize fans out to it (R5-ordered).
    Dx12RenderGraph graph;

    // PHASE-3 (chunk 20): the authored-render-feature seam. featureBridge gathers the active RenderFeatures each
    // frame (RenderFeatureManager) and, when the set changes, rebuilds the graph's feature-pass segment; featureBlitter
    // owns the proof feature's GPU blit; featureRecorder is the backend-agnostic verb surface a feature.Record drives.
    // All inert (no graph passes added) when no scene has a RenderFeatures SceneBehaviour → feature-free golden scenes
    // are byte-identical.
    Dx12FeatureBlitter featureBlitter;
    Dx12FeaturePassRecorder featureRecorder;

    Dx12RenderFeatureBridge featureBridge;

    // PHASE 2 V1 door: BALLISTIC_DX12_GRAPH=1 → run the COMPILED graph order (graph.ExecuteGraph) instead of the
    // phase-1 event-sort (graph.Execute). Default OFF → the proven phase-1 list runs unchanged (byte-identical to
    // the frozen golden set). The compiled order is provably == the event-sort order (PQ keyed (event,regIdx) +
    // AllowCulling default-OFF), so GRAPH=1 must also be byte-identical to golden (the V1 pixel-neutral bar).
    bool graphPath;

    // PHASE 2 V2 (chunk 13): the transient render-target pool + the alias-active door. aliasPath = graphPath &&
    // BALLISTIC_DX12_GRAPH_ALIAS=1. When set, rtPool owns the shared placed-resource heap + the alias plan, and is
    // published as Dx12RenderTargetPool.Active so each pooled pass's AllocOrPool hands back placed (aliased)
    // targets. BeginRender emits the per-frame whole-heap aliasing barrier before the graph runs. Default off →
    // rtPool null, Active null → committed targets, byte-identical to V1.
    Dx12RenderTargetPool rtPool;

    bool aliasPath;

    // PHASE 2 V3 (chunk 14): the auto-derived-barriers door. barriersPath = graphPath && BALLISTIC_DX12_GRAPH_BARRIERS=1.
    // When set, the graph emits each migrated pass's derived head transition (deriver.Emit) before its Record, and
    // ctx.BarriersDerived tells the migrated pass to skip its own manual head transition (emit derived ONLY).
    // Default off → migrated passes emit their manual head transitions, byte-identical to V1/V2.
    bool barriersPath;

    // Cascade caching: skip re-rendering the sun cascades when the texel-snapped fit matrices AND the caster
    // geometry are unchanged (the depth-array layers are retained → byte-identical; big win for a static camera).
    bool shadowCacheOn;
    readonly Matrix4x4[] lastCascadeMatrices = new Matrix4x4[MaxCascades];
    int lastCasterStamp;
    int lastActiveCascadeCount = -1; // invalidate the cache when the volume changes cascadeCount
    bool shadowMapEverRendered;

    // BuildTaa / AllocTaaTargets / DrawTaa moved VERBATIM into Resources/Dx12TaaPass.cs (chunk 7). The pass
    // owns the rootsig/PSO/CB/heap + ping-pong history targets + taaWriteB/taaHistoryValid, runs at the
    // PostProcess event (native path only — Enabled=!FsrActive; the TAA-skipped history reset moved into the
    // pass too). Was BuildTaa (→ AllocTaaTargets).

    // Standard 8-phase Halton(2,3) sub-pixel jitter in pixel units (-0.5..0.5). Reused by FSR later. Stays in
    // the orchestrator — it sets BeginRender's `currentJitter` (TAA + FSR jitter), not a TAA-pass-private thing.
    static Vector2 JitterOffset(int frameIndex)
    {
        int i = (frameIndex % 8) + 1;
        return new Vector2(Halton(i, 2) - 0.5f, Halton(i, 3) - 0.5f);
    }

    static float Halton(int index, int b)
    {
        float r = 0f, f = 1f;
        while (index > 0)
        {
            f /= b;
            r += f * (index % b);
            index /= b;
        }

        return r;
    }

    // BuildSsr / AllocSsrTarget moved into Resources/Dx12ReflectionsPass.cs (ctor + Resize). The reflections
    // targets are no longer reallocated inline in AllocateResolutionTargets (the graph handles it).

    // Geometry pass PSO: same vertex layout + per-draw CBV(b0) + 6 material SRVs(t0..t5) as the forward
    // opaque path, but the pixel shader (GBuffer.hlsl) writes the 5-MRT fat G-buffer (+ motion) instead of
    // shading. Adds a per-pass MotionConstants CBV(b1) for the motion-vector reprojection.
    unsafe void BuildGeometryPass()
    {
        // b0 = per-draw DrawConstants (root CBV); table0 = 6 material SRVs t0..t5; b1 = MotionConstants
        // (per pass); s0 wrap sampler.
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView,
            new RootDescriptor1(0, 0), ShaderVisibility.All);
        var matRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, MaterialSrvCount,
            baseShaderRegister: 0);
        var matTable = new RootParameter1(new RootDescriptorTable1(matRange), ShaderVisibility.Pixel);
        var motionCbv = new RootParameter1(RootParameterType.ConstantBufferView,
            new RootDescriptor1(1, 0), ShaderVisibility.Pixel);
        var wrap = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 16,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        gbufferRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout,
                new[] { cbv, matTable, motionCbv }, new[] { wrap })));

        int motionCbSize = (System.Runtime.InteropServices.Marshal.SizeOf<MotionConstants>() + 255) & ~255;
        motionCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)motionCbSize), ResourceStates.GenericRead);
        motionCbMapped = motionCb.Map<byte>(0);

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("GBuffer.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "GBuffer.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "GBuffer.hlsl");
        var layout = new InputLayoutDescription(
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 0, 1),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 0, 2),
            new InputElementDescription("TANGENT", 0, Format.R32G32B32A32_Float, 0, 3));
        gbufferPso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = gbufferRootSig, VertexShader = vs, PixelShader = ps, InputLayout = layout,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullClockwise, // back-face cull, CCW-from-front (forward parity)
            BlendState = BlendDescription.Opaque, DepthStencilState = DepthStencilDescription.Default,
            RenderTargetFormats = Dx12GBuffer.ColorFormats,
            DepthStencilFormat = Dx12GBuffer.DepthFormat, SampleDescription = new SampleDescription(1, 0),
        });
    }

    // Skinned-geometry PSO: same G-buffer target/state as BuildGeometryPass, but the vertex stage skins by
    // per-bone matrices (GBufferSkinned.hlsl). Root sig adds a bone-matrix SRV (t6, root SRV) on top of the
    // static layout (b0 DrawConstants, table0 = 6 material SRVs, b1 MotionConstants). Input layout adds two
    // streams: BLENDINDICES (slot 4) + BLENDWEIGHT (slot 5), each a float4 buffer the mesh already uploads.
    unsafe void BuildSkinnedGeometryPass()
    {
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView,
            new RootDescriptor1(0, 0), ShaderVisibility.All);
        var matRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, MaterialSrvCount,
            baseShaderRegister: 0);
        var matTable = new RootParameter1(new RootDescriptorTable1(matRange), ShaderVisibility.Pixel);
        var motionCbv = new RootParameter1(RootParameterType.ConstantBufferView,
            new RootDescriptor1(1, 0), ShaderVisibility.Pixel);
        // Bone matrices as a root SRV at t6 (vertex-visible) — a raw GPU address, no descriptor-heap slot.
        var boneSrv = new RootParameter1(RootParameterType.ShaderResourceView,
            new RootDescriptor1(6, 0), ShaderVisibility.Vertex);
        var wrap = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 16,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        skinnedGbufferRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout,
                new[] { cbv, matTable, motionCbv, boneSrv }, new[] { wrap })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("GBufferSkinned.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "GBufferSkinned.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "GBufferSkinned.hlsl");
        var layout = new InputLayoutDescription(
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 0, 1),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 0, 2),
            new InputElementDescription("TANGENT", 0, Format.R32G32B32A32_Float, 0, 3),
            new InputElementDescription("BLENDINDICES", 0, Format.R32G32B32A32_Float, 0, 4),
            new InputElementDescription("BLENDWEIGHT", 0, Format.R32G32B32A32_Float, 0, 5));
        skinnedGbufferPso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = skinnedGbufferRootSig, VertexShader = vs, PixelShader = ps, InputLayout = layout,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullClockwise,
            BlendState = BlendDescription.Opaque, DepthStencilState = DepthStencilDescription.Default,
            RenderTargetFormats = Dx12GBuffer.ColorFormats,
            DepthStencilFormat = Dx12GBuffer.DepthFormat, SampleDescription = new SampleDescription(1, 0),
        });

        // Per-frame bone-matrix upload ring: one MaxBonesPerDraw-matrix slot per skinned draw.
        boneMatrixSlotSize = (MaxBonesPerDraw * 64 + 255) & ~255; // 64 bytes per float4x4
        boneMatrixSlotCount = 64; // skinned characters per frame ceiling
        boneMatrixRing = dev.Device.CreateCommittedResource(
            HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)((long)boneMatrixSlotSize * boneMatrixSlotCount)),
            ResourceStates.GenericRead);
        boneMatrixMapped = boneMatrixRing.Map<byte>(0);
    }

    // BuildDeferredLighting moved VERBATIM into the Dx12DeferredLightingPass ctor (chunk 9): the deferred
    // rootsig/PSO/CB/13-SRV heap (LightConstants CBV b0 + FrameConstants CBV b1 + 13-SRV table + clamp sampler).
    // The pass runs at the OpaqueLighting event (300) via the graph. clusteredLights (which BuildDeferredLighting
    // used to also construct) is now built in Initialize, orchestrator-owned.

    // BuildTransparentPass / DrawTransparents moved VERBATIM into Resources/Dx12TransparentsPass.cs (chunk 8):
    // the forward transparent pass (back-to-front alpha-blended Material.Transparent submeshes, full forward PBR
    // sun+IBL+shadows+clustered punctual) + its TransparentConstants/AabbInFrustum/ToNumerics/BindSrvInto. The
    // pass owns the rootsig/PSO/CB/heap and runs at the Transparents event (450) via the graph.

    // BuildComposite / BuildLumAverage / BuildBloom / AllocBloomTargets / DrawBloom / DumpMeteredLuminance /
    // DumpAdaptedEv / DrawComposite moved VERBATIM into Resources/Dx12CompositePass.cs (chunk 7). The pass owns
    // the composite rootsig/PSO/CB/heap AND its private sub-steps' resources (auto-exposure metering + bloom),
    // runs at the Composite event via the graph (after the still-inline TAA/FSR block), reading ctx.SceneColor.

    // GTAO lives in Resources/Dx12GtaoPass.cs (replaced the old HBAO Dx12SsaoPass). The pass owns the
    // rootsig/PSOs/CB/heap/targets, runs at the AfterGBuffer event (200, BEFORE deferred lighting) via the
    // graph, and exposes its blurred AO via gtaoPass.ResultSrvCpu → ctx.AoResult for the deferred ambient term.

    // BuildFog / BuildAerialPerspective moved into the Dx12FogPass / Dx12AerialPerspectivePass ctors (chunk 5).

    unsafe void BuildShadows()
    {
        shadowMap = new Dx12ShadowMap(dev, shadowMapSize, MaxCascades);

        // Depth-only PSO: ShadowConstants CBV (b0), POSITION-only input, depth bias to cut acne.
        shadowRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout, new[]
            {
                new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0),
                    ShaderVisibility.Vertex)
            })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("ShadowDepth.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "ShadowDepth.hlsl");
        var layout = new InputLayoutDescription(
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0));
        var raster = RasterizerDescription.CullClockwise; // cull back faces (same winding as opaque)
        raster.DepthBias = 2000; // constant slope-scaled bias to fight shadow acne
        raster.SlopeScaledDepthBias = 2.5f;
        raster.DepthBiasClamp = 0f;
        var psoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = shadowRootSig, VertexShader = vs, PixelShader = default, // depth-only, no PS
            InputLayout = layout, PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            SampleMask = uint.MaxValue, RasterizerState = raster, BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.Default,
            RenderTargetFormats = System.Array.Empty<Format>(), // no color targets
            DepthStencilFormat = Dx12ShadowMap.DepthFormat,
            SampleDescription = new SampleDescription(1, 0),
        };
        shadowPso = dev.Device.CreateGraphicsPipelineState(psoDesc);

        shadowCbSlotSize = (System.Runtime.InteropServices.Marshal.SizeOf<ShadowConstants>() + 255) & ~255;
        // MaxCascades × submesh draws per frame (sized for the full cascade budget).
        shadowCbSlotCount = MaxCascades * 4096;
        shadowCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)((long)shadowCbSlotSize * shadowCbSlotCount)),
            ResourceStates.GenericRead);
        shadowCbMapped = shadowCb.Map<byte>(0);
    }

    // BuildProcSky / BuildSkybox moved VERBATIM into Resources/Dx12SkyPass.cs's ctor (chunk 8). The pass owns
    // both rootsigs/PSOs/CBs/heaps (skybox + procedural), runs at the Sky event via the graph.

    void BuildRootSignature()
    {
        // b0 = per-draw constants (root CBV);
        // table0 (param 1) = 6 material SRVs t0..t5 (per draw);
        // table1 (param 2) = 4 SRVs t6..t9: irradiance cube / prefilter cube / BRDF LUT / shadow array (frame);
        // b1 (param 3) = per-frame FrameConstants (cascade matrices + shadow params);
        // static samplers: s0 wrap (material), s1 clamp (IBL/sky).
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView,
            new RootDescriptor1(0, 0), ShaderVisibility.All);
        var matRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, MaterialSrvCount,
            baseShaderRegister: 0);
        var matTable = new RootParameter1(new RootDescriptorTable1(matRange), ShaderVisibility.Pixel);
        var iblRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 4, baseShaderRegister: 6);
        var iblTable = new RootParameter1(new RootDescriptorTable1(iblRange), ShaderVisibility.Pixel);
        var frameCbv = new RootParameter1(RootParameterType.ConstantBufferView,
            new RootDescriptor1(1, 0), ShaderVisibility.Pixel);

        var wrap = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 16,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        var clamp = new StaticSamplerDescription(ShaderVisibility.Pixel, 1, 0)
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };

        var desc = new RootSignatureDescription1(
            RootSignatureFlags.AllowInputAssemblerInputLayout,
            new[] { cbv, matTable, iblTable, frameCbv }, new[] { wrap, clamp });
        rootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(desc));
    }

    void BuildPipeline()
    {
        // Fully-qualified: the GL backend also has a BallisticEngine.EmbeddedShaderSource (ReadGlsl), and
        // this file is in namespace BallisticEngine, so the unqualified name would resolve to the GL one.
        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("StandardOpaque.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "StandardOpaque.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "StandardOpaque.hlsl");

        // Separate input slots: the engine keeps pos/normal/uv/tangent in separate GPU buffers — one
        // InputElement per slot, each at offset 0 in its own slot. (Interleaving is a later optimization.)
        var layout = new InputLayoutDescription(
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 0, 1),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 0, 2),
            new InputElementDescription("TANGENT", 0, Format.R32G32B32A32_Float, 0, 3));

        var psoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = rootSig,
            VertexShader = vs,
            PixelShader = ps,
            InputLayout = layout,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            SampleMask = uint.MaxValue,
            // RH mesh wound CCW-from-front; DX default front face is clockwise, so CullClockwise culls
            // back faces for CCW geometry (matches the cube test).
            RasterizerState = RasterizerDescription.CullClockwise,
            BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.Default,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat },
            DepthStencilFormat = Dx12OffscreenTarget.DepthFormat,
            SampleDescription = new SampleDescription(1, 0),
        };
        pso = dev.Device.CreateGraphicsPipelineState(psoDesc);
    }

    readonly System.Diagnostics.Stopwatch cpuFrameSw = new();

    // Per-pass GPU timing. Every DX12 pass is its own ExecuteSync = submit + a
    // blocking WaitForGpu (Dx12Device.ExecuteSync), so a CPU stopwatch around a pass's calls measures that
    // pass's GPU wall-time directly (the queue is idle between passes). Not a timestamp-query GPU-exclusive
    // number — it includes submit + fence-wait overhead — but it is useful for pass cost triage and needs zero
    // query-heap plumbing through every ExecuteSync. Enable with BALLISTIC_DX12_PASS_TIMING=1 (or any
    // BALLISTIC_STATS_OUT run). Recorded into RenderStats.GpuPasses, which
    // the .stats.json / `bal perf` sidecar already serializes.
    readonly System.Diagnostics.Stopwatch passSw = new();
    bool? passTimingOn;

    bool PassTimingEnabled => passTimingOn ??= (Environment.GetEnvironmentVariable("BALLISTIC_DX12_PASS_TIMING") == "1"
                                                || !string.IsNullOrWhiteSpace(
                                                    Environment.GetEnvironmentVariable("BALLISTIC_STATS_OUT")));

    // Run `body`, and if pass timing is on, record its GPU wall-time under `name` in RenderStats.GpuPasses.
    void TimePass(string name, Action body)
    {
        if (!PassTimingEnabled)
        {
            body();
            return;
        }

        passSw.Restart();
        body();
        passSw.Stop();
        RenderStats.Scene.GpuPasses.Add((name, passSw.Elapsed.TotalMilliseconds));
    }

    public override unsafe RenderMetrics BeginRender(RendererArgs args)
    {
        IViewProjectionProvider vp = args.viewProjectionProvider;
        if (vp is null || target is null)
            return default;
        cpuFrameSw.Restart(); // CPU render-submission cost (the AI-measurable frame budget)
        if (PassTimingEnabled) RenderStats.Scene.GpuPasses.Clear(); // fresh per-pass GPU timings each frame

        // Resolve the upscale mode (volume, or a BALLISTIC_DX12_FSR env override for headless A/B) and make
        // the internal render resolution + FSR context match it (reallocates targets only on a mode change).
        // Done FIRST since it can change targetW/targetH (the projection aspect + jitter scale read them).
        EnsureUpscaleTargets(ResolveUpscaleMode());

        // Camera. The provider's view (LookAt) is convention-agnostic — convert 1:1. Rebuild the
        // projection DX-style (RH, z in [0,1]) since the provider's is OpenTK GL-convention (z in [-1,1]).
        Matrix4x4 view = vp.GetViewMatrix();
        Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(
            FovYRadians, (float)targetW / targetH, CameraNear, CameraFar);
        Matrix4x4 projUnjittered = proj; // before the jitter — the shadow cascade fit uses this (stable
        // across frames so cascade caching works; shadows shouldn't jitter)
        // UNJITTERED view*proj — used for motion vectors + the froxel/SSR/post math.
        Matrix4x4 viewProjUnjittered = view * proj;

        // Sub-pixel jitter: offset the projection by a Halton amount so the whole frame (geometry + SSR +
        // shadows) is consistently jittered; TAA/FSR resolve it against history. FSR REPLACES TAA but still
        // needs jitter (it reconstructs from jittered frames). currentJitter is reused by the FSR dispatch.
        // Deterministic capture: TAA off (its Halton jitter perturbs the G-buffer + accumulates a frame-count-
        // dependent history → non-diffable). FSR also needs jitter, so deterministic mode assumes FSR off
        // (the capture recipe sets BALLISTIC_DX12_FSR=off). Edges are aliased in deterministic captures — the
        // documented trade for frame-independence (same as the GL contract).
        bool taaOn = PostFX.TaaEnabled && !fsrActive && !DeterministicCapture && !doors.Minimal;
        bool jitterOn = taaOn || fsrActive;
        currentJitter = jitterOn ? JitterOffset(taaFrame) : Vector2.Zero;
        if (jitterOn)
        {
            // NDC offset = 2 * pixelJitter / screen. DX clip y is up, so subtract for the +y pixel dir.
            proj.M31 += 2f * currentJitter.X / targetW;
            proj.M32 -= 2f * currentJitter.Y / targetH;
        }

        Matrix4x4 viewProj = view * proj; // JITTERED — geometry/SSR/etc. render with this

        // Motion-vector constants (b1): UNJITTERED current + previous view*proj. First frame (or after a
        // resize) has no valid previous frame → use the current matrix so motion = 0 everywhere.
        Matrix4x4 viewProjPrevForMotion = motionPrevValid ? motionPrevViewProj : viewProjUnjittered;
        // V2 (fixes D3): normal-map LOD bias — sample normal maps slightly coarser to clean up the residual
        // aliasing the new upload-time mip chain (Dx12Texture2D) doesn't fully catch. Default +0.5 (gentle —
        // the mip chain does the heavy lifting; preserves detail). BALLISTIC_DX12_NORMAL_LOD_BIAS tunes it.
        float normalLodBias = 0.5f;
        if (float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_NORMAL_LOD_BIAS"),
                System.Globalization.CultureInfo.InvariantCulture, out float nlb)) normalLodBias = nlb;
        *(MotionConstants*)motionCbMapped = new MotionConstants
        {
            ViewProjCur = Matrix4x4.Transpose(viewProjUnjittered),
            ViewProjPrev = Matrix4x4.Transpose(viewProjPrevForMotion),
            NormalLodBias = normalLodBias,
        };

        Vector3 camPos = vp.Transform.WorldPosition;

        // Blend the scene's active Volumes into PostFX once per frame (exposure/bloom/etc.). This is the
        // ONLY bridge from the volume framework to the live render settings — it was never wired on DX12, so
        // EDITING the Exposure (or any) volume did NOTHING and PostFX sat at its constructor defaults (EV15).
        // The composite and fog passes read PostFX, so this must run before them. BALLISTIC_DX12_VOLUMES=0
        // restores the old unwired behaviour (PostFX = defaults) for A/B.
        if (doors.Volumes)
        {
            VolumeManager.Update(camPos);
            VolumePostProcessing.Apply(VolumeManager.Stack, PostFX);
        }

        LightUniforms light = LightUniforms.Resolve();
        Vector3 lightDir = light.Direction;
        Vector3 lightColor = light.Color;
        // Golden hour (P4): a ProceduralSky reddens/dims the directional sun by the SAME atmosphere it shows.
        // ProceduralSky.SunTransmittance was never called on DX12 — the sun was the raw white-balanced colour
        // at every elevation. Multiply it in here so geometry, the IBL bake and the sun disk all warm + fade
        // at low sun. BALLISTIC_DX12_SKY_TLUT=0 keeps the old (un-reddened) sun for A/B.
        bool tlutOn = Environment.GetEnvironmentVariable("BALLISTIC_DX12_SKY_TLUT") != "0";
        if (tlutOn && ProceduralSky.Active is { } skyForSun && lightDir.LengthSquared() > 1e-8f)
        {
            var st = skyForSun.SunTransmittance(new System.Numerics.Vector3(lightDir.X, lightDir.Y, lightDir.Z));
            lightColor *= new Vector3(st.X, st.Y, st.Z);
        }

        // Honor the AUTHORED ambient. The old MathF.Max(0.05f, …) floor OVERRODE a scene's explicit choice:
        // CornellBox authors ambientIntensity 0 (a pure direct/GI test) and LightTest 0.01 (a dark point-light
        // stage), but the floor forced both to 5%, washing CornellBox milky-grey and lifting LightTest's black.
        // SceneLighting defaults AmbientIntensity to 1.0, so scenes that want ambient already have plenty; a
        // scene that sets it low/zero means it. (IBL/GI add the real bounce ambient at their own stages.)
        Vector3 ambient = vp.AmbientColor * light.AmbientIntensity;
        // The sun radiance is HDR (lux-scaled, ~80000); a fixed pre-exposure brings it into a viewable
        // range before the ACES tonemap (the GL path auto-meters EV100; this is a constant stand-in for
        // first light). Tunable via BALLISTIC_DX12_EXPOSURE while dialing against the frozen baseline.
        // 1e-5 lands the PBR path (energy-conserving ÷π diffuse) near the GL baseline brightness; the DX12
        // image is intentionally a touch dimmer (no IBL ambient / shadows yet — those are next milestones).
        float exposure = float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_EXPOSURE"),
            System.Globalization.CultureInfo.InvariantCulture, out float e)
            ? e
            : 1.0e-5f;

        // GPU-driven: collect whole-mesh renderers once (used by BOTH the shadow pass and the geometry pass).
        wholeMeshRenderers.Clear();
        if (gpuDrivenOn)
        {
            foreach (IStaticMeshRenderer r in RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection)
                if (r is { IsActive: true, IsRenderable: true } && r.SubMeshIndex < 0 && r.SharedMesh != null)
                    wholeMeshRenderers.Add(r);
        }

        // Shadows first: render the sun cascades' depth (own upload command list) before opaque. Fit with the
        // UNJITTERED proj so the cascades are stable frame-to-frame (cascade caching + no TAA shadow jitter).
        // doors.Shadows = off under BARE-MINIMUM (the deferred shadow term hard-1.0s via fc.ShadowsEnabled below).
        if (doors.Shadows)
            RenderShadows(view, projUnjittered, light);
        else
            shadowsThisFrame = false;

        // IBL: bake the env→irradiance/prefilter/BRDF from the procedural sky (re-bakes only on param
        // change). Own upload command list, before the render list. Only when a ProceduralSky is active.
        // doors.Ibl = off under BARE-MINIMUM → UseIBL=0 → deferred uses the flat-fill ambient branch.
        iblActiveThisFrame = false;
        if (doors.Ibl && ProceduralSky.Active is { } pSky)
        {
            Vector3 sunDir = lightDir.LengthSquared() < 1e-8f ? Vector3.UnitY : Vector3.Normalize(lightDir);
            float sunAngR = (DirectionalLight.Instance?.AngularDiameter ?? 0.53f) * 0.5f * (MathF.PI / 180f);
            // Transmittance LUT (re-bakes only on atmosphere-param change). Drives P5/P6 + the future shader
            // sun-tint; the CPU sun-reddening above already uses the analytic SunTransmittance.
            skyLuts.EnsureBaked(pSky.AirDensity, pSky.Haze, pSky.OzoneDensity);
            ibl.EnsureBaked(pSky, sunDir, lightColor, sunAngR);
            iblActiveThisFrame = ibl.HasBaked;
        }

        // P0a — OPEN the pipelined frame command list. Everything from here (Hi-Z, geometry, deferred, sky,
        // transparents, GI, post, composite) records into ONE list submitted once at EndFrame, replacing the
        // ~40 per-pass ExecuteSync→WaitForGpu full GPU flushes. Shadows + the IBL bake above already ran on
        // their OWN upload lists (ExecuteUpload) so they're outside this. Readbacks mid-frame (OIDN CPU path,
        // exposure-debug) use ExecuteSyncImmediate, which flushes the open list first. No-op when
        // BALLISTIC_DX12_PIPELINED=0 (then every pass submits+waits as before — the byte-identical fallback).
        dev.BeginFrame();

        // Per-frame constants (b1): cascade matrices + shadow params. The cascade layout (count, blend) and the
        // filtering/contact-shadow tail are volume-driven via PostFX (the Shadows VolumeComponent → bridge).
        // activeCascadeCount + shadowMapSize were resolved in RenderShadows (it owns the fit + any reallocation).
        var fc = new FrameConstants
        {
            Cascade0 = Matrix4x4.Transpose(cascadeMatrices[0]),
            Cascade1 = Matrix4x4.Transpose(cascadeMatrices[1]),
            Cascade2 = Matrix4x4.Transpose(cascadeMatrices[2]),
            Cascade3 = Matrix4x4.Transpose(cascadeMatrices[3]),
            CascadeBias = new Vector4(0.0015f, 0.0020f, 0.0030f, 0.0050f),
            CascadeCountF = activeCascadeCount, ShadowsEnabled = shadowsThisFrame ? 1f : 0f,
            ShadowMapTexel = 1f / shadowMapSize,
            CascadeBlend = Math.Clamp(PostFX.ShadowCascadeBlend, 0f, 0.5f),
            ShadowFiltering = PostFX.ShadowFiltering,
            ShadowSoftness = PostFX.ShadowSoftness,
            ContactShadowsOn = PostFX.ContactShadowsEnabled ? 1f : 0f,
            ContactShadowLength = PostFX.ContactShadowLength,
            ContactShadowSteps = PostFX.ContactShadowSteps,
            ContactShadowThickness = PostFX.ContactShadowThickness,
        };
        *(FrameConstants*)frameCbMapped = fc;

        int draws = 0;
        int culled = 0;
        long tris = 0;
        srvVisible.Reset();
        int slot = 0;

        // Camera frustum planes from the UNJITTERED viewProj — per-submesh cull in the geometry pass.
        ExtractFrustumPlanes(viewProjUnjittered);

        // GPU-driven: whole-mesh renderers were collected before RenderShadows; build their bindless table.
        if (gpuDrivenOn)
            gpuDriven.EnsureMaterialTable(wholeMeshRenderers);

        // Hi-Z: build the occlusion pyramid from the PREVIOUS frame's depth (before the geometry pass clears
        // it). Camera-delta gate: disable for one frame after a big jump (stale depth) + the first frame.
        bool hizEnabled = false;
        if (hizWanted && wholeMeshRenderers.Count > 0)
        {
            float camDelta = (camPos - hizLastCamPos).Length();
            hizEnabled = hizPrimed && camDelta < 2.0f;
            hizLastCamPos = camPos;
            hizPrimed = true;
            gbuffer.DepthToNonPixelShaderResource();
            gpuDriven.BuildHiZ(gbuffer.DepthSrvCpu, targetW, targetH, hizEnabled);
        }

        var fallbackDiffuse = DefaultTextures.Neutral(TextureType.Diffuse) as Dx12Texture2D;

        // === GEOMETRY PASS: fill the fat G-buffer (no lighting — GBuffer.hlsl writes albedo/normal/ORM/
        // emissive + depth). Same vertex transform + material sampling as the old forward opaque. ===
        gbuffer.RenderGeometry(cl =>
        {
            cl.SetGraphicsRootSignature(gbufferRootSig);
            cl.SetPipelineState(gbufferPso);
            cl.SetDescriptorHeaps(srvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(2, motionCb.GPUVirtualAddress); // b1 motion (per pass)
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

            foreach (IStaticMeshRenderer r in RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection)
            {
                if (r is null || !r.IsActive || !r.IsRenderable) continue;
                // Whole-mesh renderers are GPU-driven (compute cull + ExecuteIndirect) — skip them here.
                if (gpuDrivenOn && r.SubMeshIndex < 0) continue;
                // Skinned meshes draw in the dedicated skinned block below (different PSO + bone matrices).
                if (r.IsSkinned) continue;
                Mesh mesh = r.SharedMesh;
                if (mesh is null) continue;

                var vb = mesh.VertexBuffer as Dx12Buffer<GLVector3>;
                var ib = mesh.IndexBuffer as Dx12IndexBuffer;
                var nb = mesh.NormalBuffer as Dx12Buffer<GLVector3>;
                var ub = mesh.UvBuffer as Dx12Buffer<Vector2>;
                var tb = mesh.TangentBuffer as Dx12Buffer<Vector4>;
                if (vb?.Resource is null || ib?.Resource is null ||
                    nb?.Resource is null || ub?.Resource is null || tb?.Resource is null) continue;

                Matrix4x4 model = r.Transform.WorldMatrix;
                Matrix4x4 mvp = model * viewProj;

                Span<VertexBufferView> vbViews = stackalloc VertexBufferView[4];
                vbViews[0] = new VertexBufferView(vb.GpuAddress, (uint)vb.ByteSize, (uint)vb.Stride);
                vbViews[1] = new VertexBufferView(nb.GpuAddress, (uint)nb.ByteSize, (uint)nb.Stride);
                vbViews[2] = new VertexBufferView(ub.GpuAddress, (uint)ub.ByteSize, (uint)ub.Stride);
                vbViews[3] = new VertexBufferView(tb.GpuAddress, (uint)tb.ByteSize, (uint)tb.Stride);
                cl.IASetVertexBuffers(0, vbViews);
                cl.IASetIndexBuffer(new IndexBufferView(ib.GpuAddress, (uint)ib.ByteSize, Format.R32_UInt));

                int only = r.SubMeshIndex;
                int first = only >= 0 ? only : 0;
                int last = only >= 0 ? only : mesh.SubMeshes.Length - 1;

                for (int s = first; s <= last; s++)
                {
                    if ((uint)s >= (uint)mesh.SubMeshes.Length) break;
                    SubMeshData sub = mesh.SubMeshes[s];
                    if (sub.IndexCount <= 0) continue;
                    // Per-submesh frustum cull (camera frustum from the UNJITTERED viewProj).
                    mesh.GetSubMeshBounds(s, out GLVector3 lmin, out GLVector3 lmax);
                    if (!AabbInFrustum(lmin, lmax, model))
                    {
                        culled++;
                        continue;
                    }

                    Material mat = r.MaterialFor(s);
                    if (mat is null) continue;
                    // Transparent submeshes can't be deferred-shaded (no blending in a G-buffer) — they're
                    // drawn FORWARD after deferred lighting + sky (DrawTransparents). Skip them here.
                    if (mat.Transparent) continue;
                    if (slot >= cbSlotCount) break;

                    bool hasMetal = mat.Metallic is not null;
                    bool hasRough = mat.Roughness is not null;
                    bool emissive = mat.IsEmissive;
                    // The G-buffer geometry shader reads the material-shaping fields (factors, maps, flags);
                    // the per-light fields (LightDir/LightColor/Ambient/Exposure) are unused here (they live
                    // in the deferred pass now) but the struct is shared, so they're filled harmlessly.
                    var c = new DrawConstants
                    {
                        Mvp = Matrix4x4.Transpose(mvp),
                        Model = Matrix4x4.Transpose(model),
                        LightDir = lightDir, Exposure = exposure,
                        LightColor = lightColor, Metallic = mat.MetallicFactor,
                        Ambient = ambient, Roughness = mat.RoughnessFactor,
                        CameraPos = camPos, SpecularReflectance = mat.SpecularReflectance,
                        BaseColorFactor = mat.BaseColorFactor,
                        EmissiveFactor = mat.EmissiveColor * mat.EmissiveIntensity,
                        HasEmissive = emissive ? 1f : 0f,
                        NormalStrength = mat.NormalStrength, NormalFlipY = mat.NormalFlipY ? 1f : 0f,
                        HasMetallicMap = hasMetal ? 1f : 0f, HasRoughnessMap = hasRough ? 1f : 0f,
                        PackedOrm = mat.PackedOrm ? 1f : 0f, Cutout = mat.Cutout ? 1f : 0f,
                        UseIBL = iblActiveThisFrame ? 1f : 0f,
                        PrefilterMaxMip = ibl != null ? ibl.PrefilterMipCount - 1 : 0f,
                    };
                    *(DrawConstants*)(cbMapped + (long)slot * cbSlotSize) = c;
                    cl.SetGraphicsRootConstantBufferView(0,
                        cbRing.GPUVirtualAddress + (ulong)((long)slot * cbSlotSize));

                    // 6 material SRVs (t0..t5); null slots resolve to neutral defaults.
                    int tableStart = srvVisible.AllocateRange(MaterialSrvCount);
                    BindSrv(tableStart + 0, mat.Diffuse, TextureType.Diffuse, fallbackDiffuse);
                    BindSrv(tableStart + 1, mat.Normal, TextureType.Normal, null);
                    BindSrv(tableStart + 2, mat.Metallic, TextureType.Metallic, null);
                    BindSrv(tableStart + 3, mat.Roughness, TextureType.Roughness, null);
                    BindSrv(tableStart + 4, mat.AO, TextureType.AO, null);
                    BindSrv(tableStart + 5, mat.Emissive, TextureType.Emissive, null);
                    cl.SetGraphicsRootDescriptorTable(1, srvVisible.Gpu(tableStart));

                    cl.DrawIndexedInstanced((uint)sub.IndexCount, 1, (uint)sub.IndexStart, 0, 0);
                    draws++;
                    tris += sub.IndexCount / 3;
                    slot++;
                }
            }

            // === SKINNED geometry: same G-buffer, but the skinned PSO skins each vertex by per-bone matrices
            // (an Animator on the entity supplies SkinningMatrices; bind pose / identity otherwise). Switch the
            // root sig + PSO once, then draw every skinned renderer with the 6-stream layout + a bone SRV. ===
            int boneSlot = 0;
            bool skinnedStateSet = false;
            foreach (IStaticMeshRenderer r in RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection)
            {
                if (r is null || !r.IsActive || !r.IsRenderable || !r.IsSkinned) continue;
                Mesh mesh = r.SharedMesh;
                if (mesh is null || !mesh.IsSkinned) continue;
                if (boneSlot >= boneMatrixSlotCount || slot >= cbSlotCount) break;

                var vb = mesh.VertexBuffer as Dx12Buffer<GLVector3>;
                var ib = mesh.IndexBuffer as Dx12IndexBuffer;
                var nb = mesh.NormalBuffer as Dx12Buffer<GLVector3>;
                var ub = mesh.UvBuffer as Dx12Buffer<Vector2>;
                var tb = mesh.TangentBuffer as Dx12Buffer<Vector4>;
                var bib = mesh.BoneIndexBuffer as Dx12Buffer<Vector4>;
                var bwb = mesh.BoneWeightBuffer as Dx12Buffer<Vector4>;
                if (vb?.Resource is null || ib?.Resource is null || nb?.Resource is null ||
                    ub?.Resource is null || tb?.Resource is null ||
                    bib?.Resource is null || bwb?.Resource is null) continue;

                // Upload this draw's bone matrices (TRANSPOSED — the shader uses row-vector mul). The renderer
                // hands us mesh-local skinning matrices (inverseBind * worldBone); identity == bind pose.
                Matrix4[] skin = r.SkinningMatrices;
                int boneCount = skin is null ? 0 : System.Math.Min(skin.Length, MaxBonesPerDraw);
                byte* dst = boneMatrixMapped + (long)boneSlot * boneMatrixSlotSize;
                var mptr = (Matrix4x4*)dst;
                for (int b = 0; b < boneCount; b++)
                    mptr[b] = Matrix4x4.Transpose(skin[b]);
                // Any unset slot stays whatever was there; only indices < boneCount are referenced by weights.
                ulong boneGpuAddr = boneMatrixRing.GPUVirtualAddress + (ulong)((long)boneSlot * boneMatrixSlotSize);

                if (!skinnedStateSet)
                {
                    cl.SetGraphicsRootSignature(skinnedGbufferRootSig);
                    cl.SetPipelineState(skinnedGbufferPso);
                    cl.SetDescriptorHeaps(srvVisible.Heap);
                    cl.SetGraphicsRootConstantBufferView(2, motionCb.GPUVirtualAddress); // b1 motion
                    cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                    skinnedStateSet = true;
                }

                cl.SetGraphicsRootShaderResourceView(3, boneGpuAddr); // t6 bone matrices (root SRV)

                Matrix4x4 model = r.Transform.WorldMatrix;
                Matrix4x4 mvp = model * viewProj;

                Span<VertexBufferView> sViews = stackalloc VertexBufferView[6];
                sViews[0] = new VertexBufferView(vb.GpuAddress, (uint)vb.ByteSize, (uint)vb.Stride);
                sViews[1] = new VertexBufferView(nb.GpuAddress, (uint)nb.ByteSize, (uint)nb.Stride);
                sViews[2] = new VertexBufferView(ub.GpuAddress, (uint)ub.ByteSize, (uint)ub.Stride);
                sViews[3] = new VertexBufferView(tb.GpuAddress, (uint)tb.ByteSize, (uint)tb.Stride);
                sViews[4] = new VertexBufferView(bib.GpuAddress, (uint)bib.ByteSize, (uint)bib.Stride);
                sViews[5] = new VertexBufferView(bwb.GpuAddress, (uint)bwb.ByteSize, (uint)bwb.Stride);
                cl.IASetVertexBuffers(0, sViews);
                cl.IASetIndexBuffer(new IndexBufferView(ib.GpuAddress, (uint)ib.ByteSize, Format.R32_UInt));

                int sOnly = r.SubMeshIndex;
                int sFirst = sOnly >= 0 ? sOnly : 0;
                int sLast = sOnly >= 0 ? sOnly : mesh.SubMeshes.Length - 1;
                for (int s = sFirst; s <= sLast; s++)
                {
                    if ((uint)s >= (uint)mesh.SubMeshes.Length) break;
                    SubMeshData sub = mesh.SubMeshes[s];
                    if (sub.IndexCount <= 0) continue;
                    // No frustum cull here: a skinned mesh's static bind-pose bounds don't bound the animated
                    // pose, and skinned meshes are few. Draw them all.
                    Material mat = r.MaterialFor(s);
                    if (mat is null) continue;
                    if (mat.Transparent) continue;
                    if (slot >= cbSlotCount) break;

                    bool hasMetal = mat.Metallic is not null;
                    bool hasRough = mat.Roughness is not null;
                    bool emissive = mat.IsEmissive;
                    var c = new DrawConstants
                    {
                        Mvp = Matrix4x4.Transpose(mvp),
                        Model = Matrix4x4.Transpose(model),
                        LightDir = lightDir, Exposure = exposure,
                        LightColor = lightColor, Metallic = mat.MetallicFactor,
                        Ambient = ambient, Roughness = mat.RoughnessFactor,
                        CameraPos = camPos, SpecularReflectance = mat.SpecularReflectance,
                        BaseColorFactor = mat.BaseColorFactor,
                        EmissiveFactor = mat.EmissiveColor * mat.EmissiveIntensity,
                        HasEmissive = emissive ? 1f : 0f,
                        NormalStrength = mat.NormalStrength, NormalFlipY = mat.NormalFlipY ? 1f : 0f,
                        HasMetallicMap = hasMetal ? 1f : 0f, HasRoughnessMap = hasRough ? 1f : 0f,
                        PackedOrm = mat.PackedOrm ? 1f : 0f, Cutout = mat.Cutout ? 1f : 0f,
                        UseIBL = iblActiveThisFrame ? 1f : 0f,
                        PrefilterMaxMip = ibl != null ? ibl.PrefilterMipCount - 1 : 0f,
                    };
                    *(DrawConstants*)(cbMapped + (long)slot * cbSlotSize) = c;
                    cl.SetGraphicsRootConstantBufferView(0,
                        cbRing.GPUVirtualAddress + (ulong)((long)slot * cbSlotSize));

                    int tableStart = srvVisible.AllocateRange(MaterialSrvCount);
                    BindSrv(tableStart + 0, mat.Diffuse, TextureType.Diffuse, fallbackDiffuse);
                    BindSrv(tableStart + 1, mat.Normal, TextureType.Normal, null);
                    BindSrv(tableStart + 2, mat.Metallic, TextureType.Metallic, null);
                    BindSrv(tableStart + 3, mat.Roughness, TextureType.Roughness, null);
                    BindSrv(tableStart + 4, mat.AO, TextureType.AO, null);
                    BindSrv(tableStart + 5, mat.Emissive, TextureType.Emissive, null);
                    cl.SetGraphicsRootDescriptorTable(1, srvVisible.Gpu(tableStart));

                    cl.DrawIndexedInstanced((uint)sub.IndexCount, 1, (uint)sub.IndexStart, 0, 0);
                    draws++;
                    tris += sub.IndexCount / 3;
                    slot++;
                }

                boneSlot++;
            }

            // Restore the static G-buffer state for any passes after this callback that assume it.
            if (skinnedStateSet)
            {
                cl.SetGraphicsRootSignature(gbufferRootSig);
                cl.SetPipelineState(gbufferPso);
                cl.SetGraphicsRootConstantBufferView(2, motionCb.GPUVirtualAddress);
            }

            // GPU-driven whole-mesh geometry: compute cull + ExecuteIndirect + bindless materials, into the
            // same G-buffer. Uses the JITTERED viewProj for the per-draw Mvp (matches the CPU path) and the
            // UNJITTERED frustum planes for culling (byte-identical visible set).
            if (gpuDrivenOn && wholeMeshRenderers.Count > 0)
            {
                draws += gpuDriven.RenderInto(cl, wholeMeshRenderers, viewProj, frustumPlanes,
                    viewProjUnjittered, view, CameraNear, CameraFar, motionCb.GPUVirtualAddress);
                tris += gpuDriven.LastTris;
            }
        });

        // Hi-Z debug door: how many whole-mesh submeshes survived the GPU cull (frustum + Hi-Z occlusion).
        if (gpuDrivenOn && wholeMeshRenderers.Count > 0
                        && Environment.GetEnvironmentVariable("BALLISTIC_DX12_HIZ_DEBUG") == "1")
        {
            var (vis, tot) = gpuDriven.DebugVisibleCount();
            Console.WriteLine($"[HiZDebug] visible submeshes {vis}/{tot} (hizEnabled={(hizEnabled ? 1 : 0)})");
        }

        // === CLUSTERED PUNCTUAL LIGHTS: gather active point/spot lights + CPU froxel-cull (before the
        // deferred pass reads the result). Lights are raw HDR (NOT pre-exposed — composite meters them,
        // same as the sun). ===
        GatherPunctualLights(view, proj);

        // === RT SUN SHADOWS (volume-driven; DXR): trace one shadow ray per pixel against the scene BVH into
        // a mask the deferred sun term reads (replaces the cascade PCF). Opt-in via the Shadows volume's RT
        // checkbox or BALLISTIC_DX12_RT_SHADOWS=1; falls back to cascades if DXR is unavailable. Runs after
        // the G-buffer is readable, before deferred lighting. The unconditional gbuffer.ToShaderResource() that
        // used to sit here (before BOTH RT shadows and deferred) moved to the deferred pass head (chunk 9, R2);
        // since RT shadows ALSO consumes the G-buffer as an SRV (depth + world normal) and runs BEFORE deferred,
        // DrawRtShadows emits its OWN head gbuffer.ToShaderResource() (idempotent, the safety net). rtShadowsThis
        // Frame must resolve HERE — the deferred pass reads it via ctx, which is built right after. ===
        rtShadowsThisFrame = false;
        string rtsEnv = Environment.GetEnvironmentVariable("BALLISTIC_DX12_RT_SHADOWS");
        bool rtShadowsWanted = rtsEnv == "1" || (rtsEnv != "0" && PostFX.RayTracedShadows);
        if (rtShadowsWanted && EnsureRtShadows())
            DrawRtShadows(viewProj, lightDir);

        // === PHASE-1 PASS-GRAPH CONTEXT (chunks 4–9). Built ONCE here — after RT shadows + the giMode resolve,
        // and now ALSO BEFORE the deferred pass (chunk 9 moved the ctx build UP above deferred so the deferred
        // window can run from ctx) — so every mutated ctx field holds its FINAL value (fsrActive resolved
        // pre-body; iblActiveThisFrame/shadowsThisFrame/rtShadowsThisFrame set above — RT shadows just ran;
        // giMode just resolved). The graph then runs in EVENT WINDOWS at the gaps between the still-inline passes,
        // so each converted pass executes at its canonical inline position (Deferred at OpaqueLighting; Sky/AP/
        // Transparents next; Fog after GI; SSAO/TAA/FSR after SSR; Composite last). As more passes convert, the
        // windows merge into one Execute (step G). ===
        var ctx = new Dx12FrameContext
        {
            View = view, Proj = proj, ViewProj = viewProj,
            ProjUnjittered = projUnjittered, ViewProjUnjittered = viewProjUnjittered,
            CurrentJitter = currentJitter, CamPos = camPos,
            LightDir = lightDir, LightColor = lightColor, Ambient = ambient, Exposure = exposure,
            WholeMeshRenderers = wholeMeshRenderers, FrustumPlanes = frustumPlanes,
            CascadeMatrices = cascadeMatrices, // shared per-frame array, filled by RenderShadows; Fog reads it
            TargetW = targetW, TargetH = targetH, OutputW = outputW, OutputH = outputH,
            Dev = dev, Target = target, Ldr = ldr, GBuffer = gbuffer,
            Ibl = ibl, SkyLuts = skyLuts, ClusteredLights = clusteredLights,
            ShadowMap = shadowMap, GpuDriven = gpuDriven,
            RtShadowMask = rtShadowMask, // chunk 9: deferred binds it to t12 when RtShadowsThisFrame (null → fallback)
            Dxr = dxr, // chunk 10: shared DXR substrate (sceneAS/device5/rtGeometry/ddgi) for the GI + Reflections passes
            FrameCbAddress =
                frameCb.GPUVirtualAddress, // chunk 8: Transparents binds it; chunk 9: Deferred binds it too (b1 FrameConstants CBV)
            Doors = doors, PostFX = PostFX, Stats = RenderStats.Scene,
            FrameCounter = DeterministicCapture ? 0 : frameCounter, // monotonic; 0 when capturing → byte-identical
            BarriersDerived = barriersPath, // chunk 14 (V3): migrated passes skip their manual head transition when on
            // deterministic flag (grain/exposure reset) + the GTAO pass output the DEFERRED LIGHTING pass samples
            // for the ambient AO term. AoResult is a stable descriptor handle (gtaoA.ColorSrvCpu) — only its
            // contents change per frame, so binding it at ctx build is always correct.
            DeterministicCapture = DeterministicCapture,
            AoResult = gtaoPass.ResultSrvCpu,
            TaaActive = taaOn, FsrActive = fsrActive, // chunk 7: TaaPass runs in native path; FsrPass when FsrActive
            Fsr = fsr, FsrOutput = fsrOutput, MotionPrevValid = motionPrevValid, // chunk 7: FsrPass dispatch inputs
            // mutated-mid-frame fields, set to their resolved final value:
            SceneColor = fsrActive ? fsrOutput : target,
            IblActiveThisFrame = iblActiveThisFrame,
            ShadowsThisFrame = shadowsThisFrame,
            RtShadowsThisFrame = rtShadowsThisFrame,
        };

        // Seed the film-grain counter. Grain is frozen to 0 under deterministic capture.
        ctx.GrainFrame = DeterministicCapture ? 0 : frameCounter;

        // === PHASE-1 PASS-GRAPH — STEP G COLLAPSE (chunk 11): the THREE chunk-5..10 event windows merge into ONE
        // full-range graph.Execute(ctx). Every non-core pass is an IRenderPass, so the graph runs the entire
        // event-ordered list in a single call: Deferred (300) → Sky (350) → AP (400) → Transparents (450) →
        // Fog (550) → Reflections (600) → SSAO/TAA/FSR (PostProcess 650) → Composite (700) — the exact
        // inline frame sequence the event sort reproduces. Each pass emits its OWN head transition (R2). The
        // Composite pass (event 700, last) reads the resolved ctx.SceneColor after the TAA/FSR resolve.
        //
        // chunk 12 (phase 2 V1): when BALLISTIC_DX12_GRAPH=1, run the COMPILED DAG order (ExecuteGraph) instead of
        // the event sort (Execute). The compiled order is provably identical to the event sort (topo-sort PQ keyed
        // (event, registrationIndex) + AllowCulling default-OFF + edges that only run earlier-frame writers before
        // later readers), so the image is byte-identical — the V1 pixel-neutral bar. Default OFF → Execute, the
        // proven phase-1 list. Barriers are STILL the manual per-pass head transitions in both paths (V1 doesn't
        // touch barriers — that's V3).
        // PHASE 2 V2 (chunk 13): the per-PASS aliasing barriers (Dx12RenderTargetPool.PoolBarrier at the head of
        // each pooled pass's Record) handle the placed-resource tenant changes — an aliasing barrier is required
        // EACH TIME a different placed resource starts using shared memory, not once per frame. So nothing is
        // emitted here; the barrier lives WITH its consuming pass (the same Decision-4 principle as the head
        // transitions). Default off (aliasPath false) → PoolBarrier is a no-op (Active is null).

        // PHASE-3 (chunk 20): the render-feature bridge — gather active authored RenderFeatures and, ONLY when the
        // set changed, rebuild the graph's feature-pass segment (mirrors the volume bridge above). Must run BEFORE
        // Execute/ExecuteGraph (it may re-Build/re-Compile the graph). A NO-OP for feature-free scenes (Gather()==0
        // every frame → the graph stays the built-in set → byte-identical to golden), so it's unconditional here.
        featureBridge.Apply();

        if (graphPath) graph.ExecuteGraph(ctx);
        else graph.Execute(ctx);

        // Editor display path: leave the LDR composite in PixelShaderResource so the editor's ImGui pass can
        // sample it via SceneColorHandle/GameColorHandle THIS frame. The player (PresentToScreen) keeps it in
        // RenderTarget for SaveFrame's readback; either way next frame's DrawComposite transitions it back.
        if (!PresentToScreen)
            ldr.ColorToShaderResource();

        // P0a — CLOSE the pipelined frame: submit the whole recorded list ONCE + wait. (P0b drops the wait for
        // CPU↔GPU overlap.) No-op when pipelining is off / no frame was opened. After this the GPU is idle, so
        // SaveFrame's readback (headless) and PresentToScreen (player) — which run AFTER BeginRender returns —
        // see a fully-rendered frame via their own ExecuteSyncImmediate/synchronous path.
        dev.EndFrame();

        // Advance the jitter phase (used by both TAA and FSR) and remember this frame's UNJITTERED view*proj
        // for next frame's motion vectors (independent of TAA, since FSR replaces TAA but still needs motion).
        if (jitterOn) taaFrame++;
        frameCounter++; // monotonic, every frame — drives dust drift even with TAA/SSGI off
        if (frameCounter >= 1 << 24) frameCounter = 0; // wrap before float precision degrades (~16.7M frames)
        motionPrevViewProj = viewProjUnjittered;
        motionPrevValid = true;

        RenderStats.Scene.DrawCalls = draws;
        RenderStats.Scene.Triangles = tris;
        RenderStats.Scene.SubMeshesCulled = culled;
        RenderStats.Scene.CpuFrameMs = cpuFrameSw.Elapsed.TotalMilliseconds;
        return new RenderMetrics(draws, 0, (int)tris, 0, 0f);
    }

    // Gather the scene's active point/spot lights into the clustered light buffer + CPU froxel-cull. Reads
    // typed properties only (no reflection). Radiance is RAW HDR PhysicalColor (NOT pre-exposed — the DX12
    // composite auto-meters it, exactly like the sun), unlike the GL path which pre-exposes at upload.
    void GatherPunctualLights(Matrix4x4 view, Matrix4x4 proj)
    {
        clusteredLights.BeginGather();
        foreach (PointLight p in RuntimeSet<PointLight>.ReadOnlyCollection)
        {
            if (p is null || !p.IsActive) continue;
            clusteredLights.AddPoint(p.transform.WorldPosition, p.Range,
                p.PhysicalColor, p.SourceRadius);
        }

        foreach (SpotLight s in RuntimeSet<SpotLight>.ReadOnlyCollection)
        {
            if (s is null || !s.IsActive) continue;
            Vector3 dir = Vector3.Transform(Vector3.UnitZ, s.transform.WorldRotation);
            float inner = Math.Clamp(s.InnerAngle, 0f, 89f) * (MathF.PI / 180f);
            float outer = Math.Clamp(MathF.Max(s.OuterAngle, s.InnerAngle), 0f, 89.9f) * (MathF.PI / 180f);
            clusteredLights.AddSpot(s.transform.WorldPosition, dir, s.Range,
                s.PhysicalColor, MathF.Cos(inner), MathF.Cos(outer), s.SourceRadius);
        }

        clusteredLights.Cull(view, proj, targetW, targetH, CameraNear, CameraFar);
    }

    // Fullscreen deferred lighting: read the G-buffer (G0..G3 + depth, already in SRV state) + IBL +
    // shadow cascades, shade Cook-Torrance sun + split-sum IBL + clustered punctual lights, write RAW HDR
    // into `target`. Mirrors the forward StandardOpaque shading — only the inputs come from the G-buffer.
    // DrawDeferredLighting moved VERBATIM into Resources/Dx12DeferredLightingPass.Record (chunk 9). It runs at
    // the OpaqueLighting event (300) via the graph, emitting its own head gbuffer.ToShaderResource() (R2 — the
    // deferred pass is the consumer of the G-buffer-as-SRV). The LightConstants struct + the deferred rootsig/
    // PSO/CB/heap moved with it; the RT shadow mask + FrameConstants CBV come through ctx (RtShadowMask /
    // FrameCbAddress).

    // DrawTransparents moved VERBATIM into Resources/Dx12TransparentsPass.Record (chunk 8). It runs at
    // the Transparents event (450) via the graph, emitting its own head DepthToReadOnly (R2).

    // DrawTaa moved VERBATIM into Resources/Dx12TaaPass.Record (chunk 7). It runs at the PostProcess
    // event via the graph (native path only); the TAA-skipped history reset moved into the pass too.

    // The active upscale mode: the volume's PostFX.UpscaleMode, overridable by BALLISTIC_DX12_FSR for
    // headless A/B (off/nativeaa/quality/balanced/performance/ultra) — a kept test door.
    UpscaleMode ResolveUpscaleMode()
    {
        string env = Environment.GetEnvironmentVariable("BALLISTIC_DX12_FSR");
        if (string.IsNullOrEmpty(env)) return PostFX.UpscaleMode;
        return env.Trim().ToLowerInvariant() switch
        {
            "0" or "off" => UpscaleMode.Off,
            "1" or "native" or "nativeaa" => UpscaleMode.NativeAA,
            "quality" or "q" => UpscaleMode.Quality,
            "balanced" or "b" => UpscaleMode.Balanced,
            "performance" or "perf" or "p" => UpscaleMode.Performance,
            "ultra" or "ultraperformance" or "up" => UpscaleMode.UltraPerformance,
            _ => PostFX.UpscaleMode,
        };
    }

    // RunFsr moved VERBATIM into Resources/Dx12FsrPass.Record (chunk 7). The pass runs at the PostProcess event
    // (FsrActive only — mutually exclusive with TaaPass); fsr/fsrOutput stay orchestrator-owned (the internal-
    // vs-output resolution lifecycle — EnsureUpscaleTargets / native reset — is whole-frame management), passed
    // through ctx.Fsr / ctx.FsrOutput. The pass sets ctx.SceneColor = fsrOutput.

    // DrawSsr / EnsureRtReflections / DrawRtReflections moved into Resources/Dx12ReflectionsPass.cs. The
    // Reflections pass (Event=Reflections 600) does the RT-vs-SSR branch and SSR fallback. The shared
    // sceneAS/device5/rtGeometry come through ctx.Dxr (Dx12DxrShared).

    // RT sun shadows STAY inline core (the plan converts them in a later chunk). chunk 10 rewired the lazy
    // DXR-availability probe + the device5/sceneAS lazy-init onto the shared `dxr` holder (Dx12DxrShared) — the
    // SAME shared substrate the GI + Reflections passes use — so all three reuse one scene AS / device5.
    unsafe bool EnsureRtShadows()
    {
        if (!dxr.CheckAvailable("RTShadows")) return false; // shared DXR-availability probe (chunk 10)
        if (rtShadowBuilt) return true;
        rtShadowBuilt = true;

        var device5 = dxr.Device5; // shared ID3D12Device5 facet (chunk 10)

        // Global root sig: CBV(b0) + table {SRV t0 TLAS, t1 depth, t2 normal; UAV u0 mask}.
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0),
            ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 3, baseShaderRegister: 0);
        var uavRange = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 0);
        var table = new RootParameter1(new RootDescriptorTable1(srvRange, uavRange), ShaderVisibility.All);
        rtShadowRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, table })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("DxrShadows.hlsl");
        byte[] dxil = Dx12ShaderCompiler.Compile(DxcShaderStage.Library, hlsl, "", "DxrShadows.hlsl");
        var subs = new[]
        {
            new StateSubObject(new DxilLibraryDescription(dxil,
                new ExportDescription("RayGen"), new ExportDescription("Miss"), new ExportDescription("ClosestHit"))),
            new StateSubObject(new HitGroupDescription("HitGroup", HitGroupType.Triangles, "", "ClosestHit", "")),
            new StateSubObject(new RaytracingShaderConfig(4, 8)), // payload = uint Occluded
            new StateSubObject(new RaytracingPipelineConfig(1)),
            new StateSubObject(new GlobalRootSignature(rtShadowRootSig)),
        };
        rtShadowPso = device5.CreateStateObject(new StateObjectDescription(StateObjectType.RaytracingPipeline, subs));

        using ID3D12StateObjectProperties props = rtShadowPso.QueryInterface<ID3D12StateObjectProperties>();
        uint idSize = Vortice.Direct3D12.D3D12.ShaderIdentifierSizeInBytes;
        rtShadowSbt = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(RtSbtSlot * 3), ResourceStates.GenericRead);
        byte* sp = rtShadowSbt.Map<byte>(0);
        System.Runtime.CompilerServices.Unsafe.CopyBlock(sp + 0 * RtSbtSlot, (void*)props.GetShaderIdentifier("RayGen"),
            idSize);
        System.Runtime.CompilerServices.Unsafe.CopyBlock(sp + 1 * RtSbtSlot, (void*)props.GetShaderIdentifier("Miss"),
            idSize);
        System.Runtime.CompilerServices.Unsafe.CopyBlock(sp + 2 * RtSbtSlot,
            (void*)props.GetShaderIdentifier("HitGroup"), idSize);
        rtShadowSbt.Unmap(0);

        int cbSize = (System.Runtime.InteropServices.Marshal.SizeOf<RtShadowConstants>() + 255) & ~255;
        rtShadowCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        rtShadowCbMapped = rtShadowCb.Map<byte>(0);
        rtShadowHeap = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 4, shaderVisible: true);
        AllocRtShadowMask();
        return true;
    }

    void AllocRtShadowMask()
    {
        rtShadowMask?.Dispose();
        rtShadowMask = new Dx12OffscreenTarget(dev, targetW, targetH, withDepth: false,
            colorFormat: Format.R8_UNorm, colorReadable: true, allowUav: true);
    }

    // RT sun shadows: ensure the scene AS, then DispatchRays one shadow ray per pixel → rtShadowMask. The
    // mask is left in PixelShaderResource for the deferred pass (UseRtShadows). viewProj is the JITTERED
    // matrix the depth was rendered with (matches the deferred pass's world-pos reconstruction).
    unsafe void DrawRtShadows(Matrix4x4 viewProj, Vector3 lightDir)
    {
        var sceneAS = dxr.SceneAS; // shared scene AS (chunk 10)
        sceneAS.Ensure(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection);
        if (!sceneAS.Valid) return;

        // R2 / Decision 4: RT shadows is ALSO a consumer of the G-buffer-as-SRV — it reads gbuffer depth +
        // world-normal (RT1) below via its own descriptor table. Chunk 9 moved the gbuffer.ToShaderResource()
        // head transition OUT of the orchestrator and INTO the deferred pass (which runs AFTER this), so RT
        // shadows must emit its OWN head transition or it would dispatch while the G-buffer is still in
        // RenderTarget/DepthWrite state. Idempotent (no-op when an upstream already set it; the deferred pass
        // re-asserts it too). Only fires under BALLISTIC_DX12_RT_SHADOWS / the Shadows-volume RT checkbox.
        gbuffer.ToShaderResource();

        Matrix4x4.Invert(viewProj, out Matrix4x4 invVP);
        Vector3 sun = lightDir.LengthSquared() < 1e-8f ? Vector3.UnitY : Vector3.Normalize(lightDir);
        *(RtShadowConstants*)rtShadowCbMapped = new RtShadowConstants
        {
            InvViewProj = Matrix4x4.Transpose(invVP), SunDir = sun, NormalBias = 0.05f,
        };

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        sceneAS.CreateTlasSrv(rtShadowHeap.Cpu(0));
        dev.Device.CopyDescriptorsSimple(1, rtShadowHeap.Cpu(1), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, rtShadowHeap.Cpu(2), gbuffer.ColorSrvCpu(1), heapType); // world normal
        dev.Device.CreateUnorderedAccessView(rtShadowMask.RenderTarget, null, new UnorderedAccessViewDescription
        {
            Format = Format.R8_UNorm, ViewDimension = UnorderedAccessViewDimension.Texture2D,
        }, rtShadowHeap.Cpu(3));

        rtShadowMask.ColorToUnorderedAccess();
        uint idSize = Vortice.Direct3D12.D3D12.ShaderIdentifierSizeInBytes;
        dev.ExecuteSync(cl =>
        {
            cl.SetDescriptorHeaps(rtShadowHeap.Heap);
            cl.SetComputeRootSignature(rtShadowRootSig);
            cl.SetPipelineState1(rtShadowPso);
            cl.SetComputeRootConstantBufferView(0, rtShadowCb.GPUVirtualAddress);
            cl.SetComputeRootDescriptorTable(1, rtShadowHeap.Gpu(0));
            cl.DispatchRays(new DispatchRaysDescription
            {
                Width = (uint)targetW, Height = (uint)targetH, Depth = 1,
                RayGenerationShaderRecord = new GpuVirtualAddressRange
                    { StartAddress = rtShadowSbt.GPUVirtualAddress, SizeInBytes = idSize },
                MissShaderTable = new GpuVirtualAddressRangeAndStride
                {
                    StartAddress = rtShadowSbt.GPUVirtualAddress + RtSbtSlot, SizeInBytes = idSize,
                    StrideInBytes = idSize
                },
                HitGroupTable = new GpuVirtualAddressRangeAndStride
                {
                    StartAddress = rtShadowSbt.GPUVirtualAddress + 2 * RtSbtSlot, SizeInBytes = idSize,
                    StrideInBytes = idSize
                },
            });
        });
        rtShadowMask.ColorToShaderResource();
        rtShadowsThisFrame = true;
    }

    // GTAO is Dx12GtaoPass.Record. It runs at the AfterGBuffer event (200) via graph.Execute; the deferred
    // lighting pass reads its blurred AO via gtaoPass.ResultSrvCpu (ctx.AoResult) and applies it to ambient only.

    // DrawBloom / DumpMeteredLuminance / DumpAdaptedEv / DrawComposite moved VERBATIM into
    // Resources/Dx12CompositePass.cs (chunk 7). DrawComposite became Dx12CompositePass.Record; bloom +
    // auto-exposure metering are its private sub-steps. The pass runs at the Composite event via the graph.

    // DrawAerialPerspective + DrawFog moved into Dx12AerialPerspectivePass.Record / Dx12FogPass.Record
    // (chunk 5). The graph runs them at events 400 / 550 (before the SSAO PostProcess slot), the same
    // relative position as today's inline frame.

    // DrawSkybox / DrawProcSky moved VERBATIM into Resources/Dx12SkyPass.cs (chunk 8) as the two branches
    // of Dx12SkyPass.Record (ProceduralSky.Active ? DrawProcSky : DrawSkybox), behind the head DepthToReadOnly.

    // Hash of all active shadow-caster transforms — changes when geometry moves/appears (so cascade caching
    // re-renders). Camera/sun motion is caught separately by the cascade fit matrices. No reflection (typed).
    int ComputeShadowCasterStamp()
    {
        var h = new System.HashCode();
        foreach (IStaticMeshRenderer r in RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection)
        {
            if (r is null || !r.IsActive || !r.IsRenderable) continue;
            GLMatrix4 m = r.Transform.WorldMatrix;
            h.Add(m.M11);
            h.Add(m.M12);
            h.Add(m.M13);
            h.Add(m.M14);
            h.Add(m.M21);
            h.Add(m.M22);
            h.Add(m.M23);
            h.Add(m.M24);
            h.Add(m.M31);
            h.Add(m.M32);
            h.Add(m.M33);
            h.Add(m.M34);
            h.Add(m.M41);
            h.Add(m.M42);
            h.Add(m.M43);
            h.Add(m.M44);
            h.Add(r.SubMeshIndex);
        }

        return h.ToHashCode();
    }

    // Round a requested shadow-map resolution to the nearest power of two (the shadow array is square POT).
    static int SnapPow2(int v)
    {
        if (v <= 1) return 1;
        int p = 1;
        while (p < v) p <<= 1;
        // pick the closer of p and p/2 (round to nearest, not always up).
        return (p - v) < (v - (p >> 1)) ? p : (p >> 1);
    }

    // Render the sun cascades' depth (one depth-array layer per cascade) before the opaque pass. Uses the
    // dedicated upload command list (separate from the render list), then leaves the array as an SRV the
    // opaque shader samples. Cascade caching skips the pass when nothing the shadows depend on changed.
    unsafe void RenderShadows(Matrix4x4 camView, Matrix4x4 camProj, LightUniforms light)
    {
        shadowsThisFrame = false;
        if (DirectionalLight.Instance is null) return; // no sun → no shadows

        Vector3 sunTravel = -light.Direction; // light.Direction is TOWARD the light
        if (sunTravel.LengthSquared() < 1e-8f) return;

        // === Volume-driven cascade layout (Shadows VolumeComponent → PostFX). The number of cascades, the
        // shadow distance, the split shape and the per-cascade resolution are all overrides now. cascadeCount
        // selects how many of the MaxCascades-deep array we fit/render/sample (no reallocation); resolution
        // reallocates the texture lazily (rare authoring edit, not a hot path). The Shadows volume's maxDistance
        // overrides DirectionalLight.ShadowDistance so an interior volume can pull the budget close. When the
        // volume bridge is OFF (BALLISTIC_DX12_VOLUMES=0, the A/B path), PostFX sits at its constructor defaults
        // and DirectionalLight.ShadowDistance stays authoritative — preserving the pre-wiring behaviour. ===
        bool volumesDriving = doors.Volumes;
        activeCascadeCount = volumesDriving ? Math.Clamp(PostFX.ShadowCascadeCount, 1, MaxCascades) : MaxCascades;
        float shadowDistance = volumesDriving && PostFX.ShadowMaxDistance > 0f
            ? PostFX.ShadowMaxDistance : DirectionalLight.Instance.ShadowDistance;
        float splitLambda = volumesDriving ? Math.Clamp(PostFX.ShadowSplitDistribution, 0f, 1f) : 0.7f;

        // Resolution change → recreate the cascade texture (powers-of-two, clamped to the volume's range). Done
        // here (before any fit/render) so the new array is valid for this frame; invalidates the cache so the
        // first frame at the new size re-renders. Cheap no-op when unchanged. Volumes-off → keep the 2048 default.
        int wantSize = volumesDriving ? Math.Clamp(SnapPow2(PostFX.ShadowResolution), 512, 4096) : 2048;
        if (wantSize != shadowMapSize)
        {
            shadowMapSize = wantSize;
            shadowMap.Dispose();
            shadowMap = new Dx12ShadowMap(dev, shadowMapSize, MaxCascades);
            shadowMapEverRendered = false; // force a re-render at the new resolution
        }

        Dx12ShadowMath.ComputeCascades(camView, camProj, sunTravel, shadowDistance, shadowMapSize,
            cascadeMatrices, cascadeDepthRanges, splitLambda, activeCascadeCount);

        // Cascade caching: if every cascade's fit matrix AND the caster geometry are unchanged since the last
        // render, the shadow-map layers still hold valid depth — skip the whole pass (byte-identical). The
        // big static-camera win. Camera/sun motion changes the fit matrices; geometry motion changes the stamp.
        int casterStamp = ComputeShadowCasterStamp();
        bool cascadesUnchanged = shadowMapEverRendered && casterStamp == lastCasterStamp
            && activeCascadeCount == lastActiveCascadeCount;
        for (int c = 0; cascadesUnchanged && c < activeCascadeCount; c++)
            cascadesUnchanged &= cascadeMatrices[c].Equals(lastCascadeMatrices[c]);
        if (shadowCacheOn && cascadesUnchanged)
        {
            shadowsThisFrame = true; // the cached shadow map is still valid
            if (Environment.GetEnvironmentVariable("BALLISTIC_DX12_SHADOW_CACHE_DEBUG") == "1")
                Console.WriteLine("[ShadowCache] cascades unchanged — skipped re-render.");
            return;
        }

        lastCasterStamp = casterStamp;
        lastActiveCascadeCount = activeCascadeCount;
        for (int c = 0; c < activeCascadeCount; c++) lastCascadeMatrices[c] = cascadeMatrices[c];
        shadowMapEverRendered = true;

        // Fill per (cascade, submesh) LightMvp constants, mirroring the opaque iteration.
        int slot = 0;
        var fills =
            new System.Collections.Generic.List<(int cascade, Dx12Buffer<GLVector3> vb, Dx12IndexBuffer ib, int start,
                int count, int cbSlot)>();
        for (int c = 0; c < activeCascadeCount; c++)
        {
            // Cull shadow casters against THIS cascade's light frustum (a caster off-screen for the camera
            // but inside this cascade still casts — that's why we cull per the LIGHT frustum, not the camera).
            ExtractFrustumPlanes(cascadeMatrices[c]);
            foreach (IStaticMeshRenderer r in RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection)
            {
                if (r is null || !r.IsActive || !r.IsRenderable) continue;
                // Whole-mesh casters are GPU-driven (per-cascade compute cull + ExecuteIndirect) — skip here.
                if (gpuDrivenOn && r.SubMeshIndex < 0) continue;
                Mesh mesh = r.SharedMesh;
                if (mesh is null) continue;
                if (mesh.VertexBuffer is not Dx12Buffer<GLVector3> vb || vb.Resource is null) continue;
                if (mesh.IndexBuffer is not Dx12IndexBuffer ib || ib.Resource is null) continue;
                Matrix4x4 model = r.Transform.WorldMatrix;
                Matrix4x4 lightMvp = model * cascadeMatrices[c];
                int only = r.SubMeshIndex;
                int first = only >= 0 ? only : 0;
                int last = only >= 0 ? only : mesh.SubMeshes.Length - 1;
                for (int s = first; s <= last; s++)
                {
                    if ((uint)s >= (uint)mesh.SubMeshes.Length) break;
                    SubMeshData sub = mesh.SubMeshes[s];
                    if (sub.IndexCount <= 0) continue;
                    mesh.GetSubMeshBounds(s, out GLVector3 lmin, out GLVector3 lmax);
                    if (!AabbInFrustum(lmin, lmax, model)) continue; // outside this cascade
                    if (slot >= shadowCbSlotCount) break;
                    *(ShadowConstants*)(shadowCbMapped + (long)slot * shadowCbSlotSize) =
                        new ShadowConstants { LightMvp = Matrix4x4.Transpose(lightMvp) };
                    fills.Add((c, vb, ib, sub.IndexStart, sub.IndexCount, slot));
                    slot++;
                }
            }
        }

        bool gpuShadows = gpuDrivenOn && wholeMeshRenderers.Count > 0;
        if (fills.Count == 0 && !gpuShadows) return;

        dev.ExecuteUpload(cl =>
        {
            // GPU-driven whole-mesh casters: per-cascade compute cull (must precede the depth draws).
            if (gpuShadows) gpuDriven.BuildShadowCull(cl, wholeMeshRenderers, cascadeMatrices, activeCascadeCount);
            shadowMap.ToDepthWrite(cl);
            for (int c = 0; c < activeCascadeCount; c++)
            {
                shadowMap.RenderCascade(cl, c, cc =>
                {
                    // CPU per-submesh casters for this cascade.
                    cc.SetGraphicsRootSignature(shadowRootSig);
                    cc.SetPipelineState(shadowPso);
                    cc.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                    foreach (var f in fills)
                    {
                        if (f.cascade != c) continue;
                        cc.SetGraphicsRootConstantBufferView(0,
                            shadowCb.GPUVirtualAddress + (ulong)((long)f.cbSlot * shadowCbSlotSize));
                        cc.IASetVertexBuffers(0,
                            new VertexBufferView(f.vb.GpuAddress, (uint)f.vb.ByteSize, (uint)f.vb.Stride));
                        cc.IASetIndexBuffer(new IndexBufferView(f.ib.GpuAddress, (uint)f.ib.ByteSize, Format.R32_UInt));
                        cc.DrawIndexedInstanced((uint)f.count, 1, (uint)f.start, 0, 0);
                    }

                    // GPU-driven whole-mesh casters for this cascade (ExecuteIndirect into the same layer).
                    if (gpuShadows) gpuDriven.DrawShadowCascade(cc, c);
                });
            }

            shadowMap.ToShaderResource(cl);
        });
        shadowsThisFrame = true;
    }


    // Copy one material texture's persistent SRV into the shader-visible table at `visibleSlot`. A null
    // texture resolves to that slot's neutral default (DefaultTextures.Neutral) so the descriptor is
    // always valid — matching the GL Material.Activate fallback (metallic 0, roughness 1, AO 1, flat +Z
    // normal, dark emissive). `explicitFallback` lets diffuse use a white fallback.
    void BindSrv(int visibleSlot, Texture2D tex, TextureType type, Dx12Texture2D explicitFallback)
        => BindSrvInto(srvVisible, visibleSlot, tex, type, explicitFallback);

    void BindSrvInto(Dx12DescriptorHeap heap, int slot, Texture2D tex, TextureType type, Dx12Texture2D explicitFallback)
    {
        var dx = (tex as Dx12Texture2D)
                 ?? explicitFallback
                 ?? (DefaultTextures.Neutral(type) as Dx12Texture2D);
        dev.Device.CopyDescriptorsSimple(1, heap.Cpu(slot), dx.SrvCpu,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
    }

    public override void PostRenderCleanUp()
    {
        foreach (IStaticMeshRenderer r in RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection)
            if (r != null)
                r.RenderedThisFrame = false;
    }

    // Readback comes from the LDR composite (R8) — the HDR scene target isn't a valid BMP source.
    public void SaveFrame(string path) => ldr?.SaveBmp(path);

    // Raw G-buffer dump for the agent's "raw perception" (`bal gbuffer`): writes depth (linear-ish window
    // depth, R32F), world normal (RGBA16F, packed N*0.5+0.5), and albedo (RGBA8 sRGB) as raw little-endian
    // .bin files + a manifest.json describing dims/format/encoding so the agent can decode them. Reads the
    // G-buffer AFTER a frame (resources are in ShaderRead state). Returns the manifest object (for the CLI).
    public object DumpGBuffer(string dir)
    {
        if (gbuffer == null) return new { ok = false, error = "no g-buffer (renderer not initialized)" };
        System.IO.Directory.CreateDirectory(dir);
        int w = gbuffer.Width, h = gbuffer.Height;

        byte[] depth = gbuffer.ReadbackRaw(-1, out int depthBpp); // R32_Float, 4 B/px
        byte[] normal = gbuffer.ReadbackRaw(1, out int normalBpp); // RGBA16F, 8 B/px (packed)
        byte[] albedo = gbuffer.ReadbackRaw(0, out int albedoBpp); // RGBA8 sRGB, 4 B/px

        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "depth.bin"), depth);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "normal.bin"), normal);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "albedo.bin"), albedo);

        return new
        {
            ok = true, width = w, height = h,
            buffers = new object[]
            {
                new
                {
                    name = "depth", file = "depth.bin", format = "R32_Float", bytesPerPixel = depthBpp,
                    encoding = "window depth [0,1]; world pos = unproject(uv, depth) via InvViewProj"
                },
                new
                {
                    name = "normal", file = "normal.bin", format = "R16G16B16A16_Float", bytesPerPixel = normalBpp,
                    encoding = "world normal PACKED as N*0.5+0.5 in RGB (half floats); unpack N = rgb*2-1"
                },
                new
                {
                    name = "albedo", file = "albedo.bin", format = "R8G8B8A8_UNorm_sRGB", bytesPerPixel = albedoBpp,
                    encoding = "albedo.rgb sRGB; a = specular F0"
                },
            },
        };
    }

    // HDR scene-color dump for the W3 noise-floor measurement (`BALLISTIC_DX12_HDR_DUMP=<file>`): read the
    // canonical HDR scene-color target (`target`, the R16F surface opaque/sky/fog render into, the same one
    // ReadColorRgb feeds OIDN) back to float RGB and write it as a raw little-endian R32F-triple .bin so the
    // floor can be measured in LINEAR/HDR space, not just the tonemapped LDR PNG (tonemap compresses sub-
    // floor HDR diffs — a barrier-induced HDR diff can round to the same 8-bit value). Measurement-only: a
    // readback at end-of-frame behind an env door, exactly like DumpGBuffer; the render path is unchanged.
    // SceneColor is the FSR/native canonical target; this reads `target` (the pre-FSR HDR composite input),
    // which is what the deterministic (FSR-off) gate exercises.
    public object DumpHdrColor(string file)
    {
        if (target == null) return new { ok = false, error = "no HDR target (renderer not initialized)" };
        int w = target.Width, h = target.Height;
        var rgb = new float[w * h * 3];
        target.ReadColorRgb(rgb); // half->float, raw HDR (no tonemap), top-down rows
        var bytes = new byte[rgb.Length * 4];
        Buffer.BlockCopy(rgb, 0, bytes, 0, bytes.Length);
        System.IO.File.WriteAllBytes(file, bytes);
        return new
        {
            ok = true, width = w, height = h, channels = 3,
            format = "R32_Float (little-endian), 3 floats/pixel (RGB), top-down rows",
            file,
        };
    }

    // Output (display/readback) resolution — equals the internal render res unless FSR is upscaling.
    public int Width => outputW;
    public int Height => outputH;

    // Internal pipeline steps — no engine/editor caller (BeginRender draws opaques itself).
    public override void RenderOpaque(IReadOnlyCollection<IStaticMeshRenderer> renderTargets,
        RendererArgs args, bool isShadowPass)
    {
    }

    public override void RenderSkybox(IReadOnlyCollection<ISkyboxDrawable> renderTargets, RendererArgs args)
    {
    }

    public override void RenderInstancing(BatchGroup<IStaticMeshRenderer> batchGroup, RendererArgs args)
    {
    }

    public override void RenderInstancing(Mesh mesh, Material material, GLMatrix4[] transforms, RendererArgs args)
    {
    }

    // --- Frustum culling (CPU, per submesh) ------------------------------------------------------------
    // 6 frustum planes (xyz = normal, w = d) extracted from a row-major view*proj (Gribb-Hartmann). Tested
    // with the positive-vertex / 8-corner-AABB rule. Mirrors the GL per-submesh cull so the geometry pass
    // and shadow pass only draw what the (camera or light) frustum can see.
    readonly Vector4[] frustumPlanes = new Vector4[6];

    void ExtractFrustumPlanes(Matrix4x4 m)
    {
        // Row-major System.Numerics: rows are (M11..M14), (M21..M24), ... Gribb-Hartmann combines rows.
        // left = row4 + row1, right = row4 - row1, bottom = row4 + row2, top = row4 - row2,
        // near = row3 (DX z[0,1]: near = row3, not row4+row3), far = row4 - row3.
        Vector4 r1 = new(m.M11, m.M21, m.M31, m.M41);
        Vector4 r2 = new(m.M12, m.M22, m.M32, m.M42);
        Vector4 r3 = new(m.M13, m.M23, m.M33, m.M43);
        Vector4 r4 = new(m.M14, m.M24, m.M34, m.M44);
        frustumPlanes[0] = r4 + r1; // left
        frustumPlanes[1] = r4 - r1; // right
        frustumPlanes[2] = r4 + r2; // bottom
        frustumPlanes[3] = r4 - r2; // top
        frustumPlanes[4] = r3; // near (DX: z >= 0)
        frustumPlanes[5] = r4 - r3; // far
        for (int i = 0; i < 6; i++)
        {
            Vector3 n = new(frustumPlanes[i].X, frustumPlanes[i].Y, frustumPlanes[i].Z);
            float len = n.Length();
            if (len > 1e-6f) frustumPlanes[i] /= len;
        }
    }

    // True if the world-space AABB (8 corners of the local box transformed by `model`) is at least partly
    // inside the frustum. Positive-vertex test: for each plane, if the farthest-along-the-normal corner is
    // behind the plane, the whole box is outside.
    bool AabbInFrustum(GLVector3 localMin, GLVector3 localMax, Matrix4x4 model)
    {
        // Transform the 8 corners to world, take their AABB (cheap + matches the GL whole-corner loop).
        Vector3 wlo = new(float.MaxValue), whi = new(float.MinValue);
        for (int c = 0; c < 8; c++)
        {
            var lc = new Vector3((c & 1) == 0 ? localMin.X : localMax.X,
                (c & 2) == 0 ? localMin.Y : localMax.Y,
                (c & 4) == 0 ? localMin.Z : localMax.Z);
            Vector3 w = Vector3.Transform(lc, model);
            wlo = Vector3.Min(wlo, w);
            whi = Vector3.Max(whi, w);
        }

        for (int i = 0; i < 6; i++)
        {
            Vector4 p = frustumPlanes[i];
            // Positive vertex (farthest along the plane normal).
            Vector3 pv = new(p.X >= 0 ? whi.X : wlo.X, p.Y >= 0 ? whi.Y : wlo.Y, p.Z >= 0 ? whi.Z : wlo.Z);
            if (p.X * pv.X + p.Y * pv.Y + p.Z * pv.Z + p.W < 0f) return false; // fully outside this plane
        }

        return true;
    }
}
