using System;
using System.Numerics;
using System.Runtime.InteropServices;
using BallisticEngine;          // RuntimeSet, IStaticMeshRenderer
using Vortice.Direct3D;         // PrimitiveTopology
using Vortice.Direct3D12;
using Vortice.Dxc;              // DxcShaderStage
using Vortice.DXGI;             // Format, SampleDescription

namespace BallisticEngine.DX12;

// Aurora V2 — the single product-facing GI pass (plan §Target Shape: one `Aurora` path; screen traces first,
// hardware RT for off-screen hits, surface/radiance cache for stable indirect). Event = GlobalIllumination
// (500), the slot the legacy GI pass occupied (after Transparents, before Fog).
//
// P2 (THIS milestone — "minimal truthful GI"): one diffuse bounce, NO surface cache, NO temporal history.
//   1. CSTrace (AuroraGi.hlsl) integrates incoming diffuse irradiance per pixel: screen-trace the depth buffer
//      first (free near-field contact bounce), inline-RayQuery the scene TLAS on a screen miss (off-screen +
//      occluded), sky/IBL on an RT miss. RT hits are shaded with REAL first-bounce radiance (emissive + sun
//      + punctual, shadow-rayed, × bindless albedo). Writes incoming irradiance E into `indirect`.
//   2. PSCombine adds E*albedo*ao/PI into the HDR scene color (additive One/One). The deferred pass already
//      suppressed its IBL diffuse ambient (ctx.AuroraActiveThisFrame → UseIBLDiffuse=0), so no double count.
// "Noisy but truthful": low ray count, no denoise. Gates are correctness — black room black, color bleed
// bleeds, thin wall no leak. Cards (P3) + radiance cache/temporal (P4) build on this.
//
// Owns the Aurora scene substrate (Dx12AuroraScene) and `indirect`. Gated behind BALLISTIC_DX12_AURORA; default-
// off = no substrate alloc + no-op Record. HW-RT only (plan gate #6: no hidden SSGI fallback).
public sealed class Dx12AuroraGiPass : IRenderPass, IDisposable
{
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.GlobalIllumination;
    public string Name => "Aurora GI";

    readonly Dx12Device dev;
    readonly Dx12AuroraScene scene;
    readonly Dx12EmissiveLights emissiveLights;   // FAZ 3d: world-space emissive-triangle area-light list (NEE)

    // FAZ 4: NVIDIA NRD (ReBLUR) temporal denoiser. When BALLISTIC_DX12_AURORA_NRD=1 and NRD.dll is present, NRD
    // owns the spatiotemporal accumulation (replacing Aurora's probe-EMA + à-trous): the trace writes raw E +
    // hit-dist (NRD-packed), a guide-pack pass produces viewZ/normal/motion, NRD denoises, an unpack writes the
    // clean E into indirectFiltered. Lazy-init on first NRD-mode frame; self-disables to Aurora temporal on failure.
    Dx12NrdDenoiser nrd;
    bool nrdTried, nrdReady;
    static int nrdDoor = -2;
    static bool NrdMode() {
        if (nrdDoor == -2) nrdDoor = Environment.GetEnvironmentVariable("BALLISTIC_DX12_AURORA_NRD") == "1" ? 1 : 0;
        return nrdDoor == 1;
    }
    // NRD-mode resources (lazy-built in EnsureNrd; all behind the NRD door so NRD-off is byte-identical).
    ID3D12RootSignature nrdTraceRootSig, nrdPackRootSig, nrdUnpackRootSig;
    ID3D12PipelineState nrdTracePso, nrdPackPso, nrdUnpackPso;
    Dx12FrameCb<AuroraNrdConstants> nrdConstCb;
    Dx12FrameCb<NrdPackConstants> nrdPackCb;
    ID3D12Resource nrdRadianceHitDist, nrdViewZ, nrdNormalRough, nrdMotion, nrdOut;
    ID3D12Resource nrdViewZPrev;   // last frame's linear viewZ (ping-ponged with nrdViewZ) → true 2.5D motion .z
    bool nrdViewZPrevValid;
    Dx12DescriptorHeap nrdScratch;   // pack (2 SRV+3 UAV) + unpack (2 SRV+1 UAV) tables, per frame

    [StructLayout(LayoutKind.Sequential)]
    struct AuroraNrdConstants { public Matrix4x4 NrdViewMatrix; public Vector3 NrdHitDistParams; public float Pad; }
    [StructLayout(LayoutKind.Sequential)]
    struct NrdPackConstants {
        public Matrix4x4 InvViewProj; public Matrix4x4 PrevViewProj; public Matrix4x4 ViewMatrix;
        public Vector2 InvResolution; public float PrevViewZValid; public float Pad;
    }

    // The card radiance cache (+ per-instance meta) the Reflections pass (event 600, after this) samples so
    // rough reflections read the SAME multi-bounce GI the diffuse sees (plan P5). Exposed read-only; valid only
    // after a successful Ensure this frame (the reflections pass also gates on ctx.AuroraActiveThisFrame).
    public Dx12AuroraScene Scene => scene;

    public Dx12AuroraGiPass(Dx12Device device, int width, int height)
    {
        dev = device;
        scene = new Dx12AuroraScene(device);
        emissiveLights = new Dx12EmissiveLights(device);
        BuildPipelines();
        Resize(width, height);
    }

    // The product door. Aurora is driven by the GlobalIllumination VOLUME (ctx.PostFX.AuroraEnabled, default ON —
    // plan §Target Shape: one product-facing mode). The BALLISTIC_DX12_AURORA env door overrides for A/B:
    // "1" forces on, "0" forces off, unset → follow the volume. Always hard-gated by hardware ray tracing in
    // WouldRun (no HW RT → Aurora unavailable, plan gate #6: NO hidden screen-space fallback).
    static int envDoor = -2;   // -2 unread, -1 unset(follow volume), 0 force-off, 1 force-on
    static bool Armed(Dx12FrameContext ctx) {
        if (envDoor == -2) {
            string v = Environment.GetEnvironmentVariable("BALLISTIC_DX12_AURORA");
            envDoor = v == "1" ? 1 : v == "0" ? 0 : -1;
        }
        return envDoor == 1 || (envDoor == -1 && ctx.PostFX.AuroraEnabled);
    }

    // ---- ASYNC (1-frame-delayed) GI door ----
    // BALLISTIC_DX12_AURORA_ASYNC_GI=="1" decouples the GI trace chain from the combine: the combine adds the
    // PREVIOUS frame's traced indirect into THIS frame's HDR color, then the (heavy) trace/denoise/temporal chain
    // for THIS frame is recorded AFTER the combine so it overlaps the rest of the frame's graphics. Default OFF →
    // the combine reads this frame's freshly-traced indirect, byte-identical to the legacy single-phase Record.
    // ALWAYS forced off under a deterministic capture (a 1-frame delay would shift the golden — paused diffs must
    // stay byte-identical).
    static int asyncGiDoor = -2;   // -2 unread, 0 off (default), 1 on
    static bool AsyncGiDoorRaw() {
        if (asyncGiDoor == -2)
            asyncGiDoor = Environment.GetEnvironmentVariable("BALLISTIC_DX12_AURORA_ASYNC_GI") == "1" ? 1 : 0;
        return asyncGiDoor == 1;
    }
    // The real compute-queue hand-off (RecordAsyncCompute) is gated additionally on the async-compute INFRA being
    // up (BALLISTIC_DX12_ASYNC_COMPUTE=1). Door on but infra off → the trace runs inline (RecordAsyncCompute falls
    // back) so a runtime-correct path with no overlap; byte-identical to door-off when neither is set.
    static bool AsyncGiArmed(Dx12FrameContext ctx) => AsyncGiDoorRaw() && !ctx.DeterministicCapture;

    // Frame-independent "the async-GI door is set" predicate, read by Dx12ClusteredLights so it YIELDS the single
    // per-frame async-compute hand-off to Aurora (forces its own cull inline) — both passes target the SAME hand-off
    // (frameSplitThisFrame allows only one), and the Aurora trace is the far larger overlap win. See the collision
    // note in Dx12ClusteredLights.GpuCull. The deterministic guard lives in AsyncGiArmed (the door alone here is
    // frame-independent; under a deterministic capture Aurora runs inline anyway, and so should cull → this is fine).
    public static bool AsyncGiDoorOn => AsyncGiDoorRaw();

    // The frame-level "Aurora runs" predicate, shared with the orchestrator (which mirrors it into
    // ctx.AuroraActiveThisFrame so the deferred pass suppresses its IBL diffuse ambient before this pass adds
    // its own diffuse indirect). Aurora is HW-RT only — no hidden SSGI fallback (plan gate #6).
    // MUTUAL EXCLUSION with Lumen (FAZ 0): both passes register at event 500; only one may run per frame. Lumen
    // takes precedence — when BALLISTIC_DX12_LUMEN=1 (Dx12LumenGiPass.Armed), Aurora YIELDS so the Lumen pass runs
    // instead. When the Lumen door is unset (default), Armed is false → this term is a no-op and Aurora is byte-
    // identical to before. This is the single arbitration point: Lumen.WouldRun does NOT inspect Aurora.
    public static bool WouldRun(Dx12FrameContext ctx) =>
        !ctx.Doors.Minimal && Armed(ctx) && !Dx12LumenGiPass.Armed(ctx)
        && ctx.Dev.HasHardwareRayTracing && ctx.Dxr?.SceneAS != null;

    public bool Enabled(Dx12FrameContext ctx) => WouldRun(ctx);

    // ---- trace (inline RayQuery compute) ----
    ID3D12RootSignature traceRootSig;   // HeapDirectlyIndexed; CBV b0/b1 + table{t0-t6, u0} + root SRV t7/t8/t9 + s0/s1
    ID3D12PipelineState tracePso;
    // P0b: N-buffered (FrameSlot-offset) so frame overlap can't stomp the GI constants the GPU still reads.
    // Pure upload-slab N-buffering — the Aurora ALGORITHM (trace/blend/gather/shading) is untouched; only WHICH
    // copy of the per-frame constants the CPU writes + the GPU binds changes. FramesInFlight==1 → byte-identical.
    Dx12FrameCb<AuroraConstants> traceCb;
    Dx12FrameCb<AuroraSun> sunCb;
    const int AuroraTableBase = Dx12BindlessTail.AuroraTableBase;
    Dx12OffscreenTarget indirect;       // probe-res RGBA16F incoming irradiance E (cross-pass scratch; rebuilt on resize)

    // ---- #3 PROBE TEMPORAL ACCUMULATION ----
    // The trace is a low-res PROBE gather (1 trace point per probe-grid cell). A few rays/probe/frame is noisy, so
    // the probe radiance is ACCUMULATED across frames (cache-space-like temporal EMA) → many effective rays at low
    // cost, the low-variance final gather Aurora's screen probes give. History is probe-res, depth-guarded against a
    // disocclusion (camera move / geometry change flushes a probe instead of smearing). `probeHistory` holds last
    // frame's accumulated E + its depth in .a; cross-frame so it is pass-owned (NEVER pooled). RGBA16F: rgb=E, a=depth.
    Dx12OffscreenTarget probeHistory;
    bool probeHistoryValid;

    // ---- COMMON motion-vector temporal resolve (AuroraTemporal.hlsl) — the proper motion-stability fix ----
    // Runs on `indirect` after the trace/gather, reprojecting the resolved history with the REAL G-buffer motion
    // vector (RT4) + neighbourhood AABB clamp + disocclusion reject. Replaces both the per-pixel inline temporal
    // (camera-only) and gives the screen-probe path a temporal it never had. `probeHistory` is the resolved history.
    ID3D12RootSignature tempRootSig;   // CBV b0 + table{t0 InE, t1 History, t2 Depth, t3 Motion, u0 OutE} + sampler
    ID3D12PipelineState tempPso;
    Dx12FrameCb<TemporalConstants> tempCb;   // P0b N-buffered
    Dx12DescriptorHeap tempSrv;        // 5 descriptors/frame
    Dx12OffscreenTarget indirectResolved; // temporal output (ping target so we don't read+write `indirect` in place)

    [StructLayout(LayoutKind.Sequential)]
    struct TemporalConstants { public Vector2 Texel; public float HistoryValid; public float Alpha; public uint W, H; public float Pad0, Pad1; }

    // ---- spatial denoise (edge-aware blur of the per-pixel indirect E) ----
    ID3D12RootSignature denoiseRootSig; // CBV b0 + table{t0-t2 SRV, u0 UAV}
    ID3D12PipelineState denoisePso;
    // (per-pass denoise CBs live in denoiseCbs[]/denoiseCbMappedArr[] — see BuildDenoisePipeline)
    Dx12DescriptorHeap denoiseSrv;      // 4 descriptors (E/depth/normal SRV + filtered UAV)
    Dx12OffscreenTarget indirectFiltered; // full-res filtered E the combine reads (the trace/temporal write target)

    // ---- ASYNC GI double-buffer ----
    // When the async-GI door is ON the combine adds the PREVIOUS frame's filtered E, so the trace chain (which
    // writes the CURRENT frame's filtered E) can be re-ordered after the combine (decoupled). Two filtered
    // buffers ping-pong by frame: `indirectFiltered` is the trace WRITE target this frame, `indirectFilteredB`
    // holds last frame's result the combine READS. They swap at the END of Record. When the door is OFF the B
    // buffer is never touched (single-buffer legacy path), so default behaviour is byte-identical.
    Dx12OffscreenTarget indirectFilteredB;   // the "previous frame" filtered E the async combine reads
    bool asyncHistoryValid;                  // false until the trace has filled at least one buffer (first async frame skips combine)

    [StructLayout(LayoutKind.Sequential)]
    struct DenoiseConstants { public Vector2 Texel; public float Step; public float Enabled; }

    // ---- combine (additive fullscreen) ----
    ID3D12RootSignature combineRootSig; // 4-SRV table + sampler
    ID3D12PipelineState combinePso;
    ID3D12PipelineState combineDebugPso; // OPAQUE replace — BALLISTIC_DX12_AURORA_DEBUG=1 shows raw E (no add)
    Dx12FrameCb<CombineConstants> combineCb;   // P0b N-buffered
    Dx12DescriptorHeap combineSrv;      // 5 SRVs per pass (E/albedo/material/depth/GTAO)

    [StructLayout(LayoutKind.Sequential)]
    struct CombineConstants { public float AoStrength; public Vector2 IndirectTexel; public float Pad0; }   // IndirectTexel = 1/half-res for the depth-aware upsample

    [StructLayout(LayoutKind.Sequential)]
    struct AuroraConstants
    {
        public Matrix4x4 InvViewProj;
        public Matrix4x4 ViewProj;
        public Vector3 CameraPos; public float Intensity;
        public Vector2 TexelSize; public float RayCount; public float FrameIndex;
        public float NormalBias; public float MaxRayDist; public float UseCards; public float ScreenSteps;
        public float SkyIntensity; public float UseSky; public float UseScreenTrace; public float ScreenRange;
        public float HistoryValid; public float ProbeAlpha; public float ImportanceSampling; public float TexelDim;   // #3 temporal; #4 importance; Sıra 5 mesh-card grid edge
        public Matrix4x4 PrevViewProj;   // #3: previous-frame UNJITTERED view*proj — camera-motion-robust probe reprojection
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AuroraSun { public Vector3 SunDir; public float SunBias; public Vector3 SunColor; public float LightCount; }

    // Card-lighting pass (AuroraCardLight.hlsl): lights every triangle "card" before the trace samples them.
    ID3D12RootSignature cardRootSig;
    ID3D12PipelineState cardPso;
    Dx12FrameCb<AuroraCardConstants> cardCb;   // P0b N-buffered
    const int CardSkyTableBase = Dx12BindlessTail.AuroraCardTableBase;

    [StructLayout(LayoutKind.Sequential)]
    struct AuroraCardConstants
    {
        public Vector3 SunDir; public float SunBias;
        public Vector3 SunColor; public float LightCount;
        public uint InstanceCount; public uint TotalTris; public float SkyIntensity; public float UseSky;
        public float SkyVisRays; public float EmaAlpha; public float BounceRays; public float HistoryValid;
        public uint FrameIndex; public uint UpdateStride; public uint ForceFull; public uint TexelDim;   // P7 #1; Sıra 5 mesh-card grid edge
        public Vector3 CameraPos; public float PriorityScale;   // P7 #1b priority budget
        public float PriorityNearDist; public float UsePriority; public float EmissiveCount; public float NeeIntensity;   // FAZ 3d NEE
    }

    // ---- Sıra 1: SCREEN-SPACE RADIANCE PROBES (AuroraScreenProbe.hlsl) ----
    // A sparse grid of radiance probes (one per ProbeStride×ProbeStride screen tile) replaces the per-pixel trace
    // as the GI front end: far fewer trace points + more rays each → lower variance AND lower cost (the published
    // Aurora final-gather). Three compute passes (place → trace → integrate) write the SAME full-res `indirect`
    // irradiance E buffer the per-pixel CSTrace did, so the downstream probe-temporal + denoise + combine chain is
    // untouched. Gated behind BALLISTIC_DX12_AURORA_SCREENPROBE ("1" force on, "0" force off, unset → default).
    ID3D12RootSignature spRootSig;       // HeapDirectlyIndexed; CBV b0/b1 + root SRV t0 TLAS + table{t1-t6, u1 atlas} + root SRV t7-t12 + root UAV u0 headers / u2 indirect + s0/s1
    ID3D12PipelineState spPlacePso, spTracePso, spIntegratePso, spFilterPso, spShPso;
    ID3D12Resource probeSH;   // SH irradiance cache: 7 float4 / probe (9 RGB cosine-convolved coeffs), root UAV u4
    int probeShCapacity;      // current probe count the buffer is sized for
    Dx12FrameCb<ProbeConstants> spProbeCb;   // P0b N-buffered
    Dx12FrameCb<AuroraSun> spSunCb;           // P0b N-buffered
    ID3D12Resource probeHeaders;         // StructuredBuffer<ProbeHeader> (root UAV u0) — sized ProbesX*ProbesY
    ID3D12Resource probeHeadersPrev;     // Sıra 3: previous frame's headers (reproject reject) — root SRV t16
    Dx12OffscreenTarget probeAtlas;      // octahedral radiance atlas, (ProbesX*OctSize) × (ProbesY*OctSize), RGBA16F UAV
    Dx12OffscreenTarget probeAtlasFiltered; // probe-space spatial-filtered atlas (blob fix) — the integrate reads this
    Dx12OffscreenTarget probeAtlasHistory; // Sıra 3: previous frame's accumulated atlas (EMA source) — table t13
    bool spHistoryValid;
    int probeStride = 16, octSize = 8;
    int probesX, probesY, probeHeaderCount;
    const int SpTableBase = Dx12BindlessTail.AuroraScreenProbeTableBase;
    // PERF: the screen-probe table descriptors (t1-t6 SRV + u1/u2/u3 UAV) are re-written into the bindless tail
    // EVERY frame even though the source resources don't change between resizes — CreateUnorderedAccessView +
    // CopyDescriptorsSimple are CPU driver calls and were ~half the per-frame Aurora CPU cost. Cache them: only
    // re-write when a source resource HANDLE changes (resize / scene swap), detected by this stamp. Visual output
    // is byte-identical (the descriptors point at the same views).
    long spDescStamp = -1;

    [StructLayout(LayoutKind.Sequential)]
    struct ProbeConstants
    {
        public Matrix4x4 InvViewProj;
        public Matrix4x4 ViewProj;
        public Vector3 CameraPos; public float Intensity;
        public Vector2 FullTexel; public float RayCount; public float FrameIndex;
        public float NormalBias; public float MaxRayDist; public float UseCards; public float ScreenSteps;
        public float SkyIntensity; public float UseSky; public float UseScreenTrace; public float ScreenRange;
        public float FalloffDist; public float UseSH; public float ProbeStride; public float OctSize;
        public uint ProbesX; public uint ProbesY; public uint FullW; public uint FullH;
        public float HistoryValid; public float ProbeEma; public float TexelDim; public float SpPad1;
        public float AdaptiveRays; public float AdaptiveStride; public float AdaptiveVar; public float SpPad2;
    }

    static int spEnvDoor = -2;   // -2 unread, -1 unset(default), 0 force-off, 1 force-on
    bool WantScreenProbe(Dx12FrameContext ctx)
    {
        if (spEnvDoor == -2)
        {
            string v = Environment.GetEnvironmentVariable("BALLISTIC_DX12_AURORA_SCREENPROBE");
            spEnvDoor = v == "1" ? 1 : v == "0" ? 0 : -1;
        }
        if (spEnvDoor == 1) return true;    // explicit force-on (overrides the deterministic guard, for A/B)
        if (spEnvDoor == 0) return false;
        // DEFAULT path. Screen probes are the GI front end — they are PERF-CRITICAL (far fewer trace points than
        // per-pixel, the user needs this for FPS). The user's live test found per-pixel cleaner in MOTION but
        // explicitly does NOT accept its perf cost → the right answer is to KEEP screen probes and FIX their motion
        // boiling (sliding blobs under camera motion — the sparse probe grid's per-frame placement + few-ray gather
        // don't accumulate under motion). Deterministic capture still falls back to per-pixel (golden stability).
        return ScreenProbeDefaultOn && !ctx.DeterministicCapture;
    }
    // Screen probes are the default — they're perf-critical (the user's requirement). Motion boiling is being
    // fixed (motion-vector reprojected history + lean on the view-independent card cache).
    const bool ScreenProbeDefaultOn = true;

    int frameCounter;

    // ---- ASYNC trace-phase command-list sink ----
    // When the async-GI hand-off is taken, the ENTIRE trace phase (TraceScreenProbe / per-pixel trace + denoise +
    // temporal + every copy/transition between them) is recorded into ONE compute command list passed by
    // RecordAsyncCompute. The trace code is written queue-agnostically: every GPU command goes through Emit(), every
    // texture state change through ToSrv/ToNonPixel/ToUav, every texture copy through Copy(). When `asyncCl` is null
    // (the door-off / inline path) those helpers fall back to the per-call dev.ExecuteSync + Dx12OffscreenTarget
    // methods — byte-identical to the legacy code, since each helper expands to the exact same ExecuteSync it
    // replaced. When `asyncCl` is non-null they append to the single compute list instead (no submit between them).
    ID3D12GraphicsCommandList4 asyncCl;

    // Emit a block of GPU commands: append to the open async compute list, or run as its own graphics submit inline.
    void Emit(Action<ID3D12GraphicsCommandList4> rec) { if (asyncCl != null) rec(asyncCl); else dev.ExecuteSync(rec); }
    // Texture state helpers — on the async path a compute list can ONLY reach UAV / NonPixelSRV / Copy states
    // (PixelShaderResource is graphics-only), so the SRV read state is NonPixel there; inline keeps the legacy state.
    void ToShaderRead(Dx12OffscreenTarget t) { if (asyncCl != null) t.ColorTransitionInList(asyncCl, ResourceStates.NonPixelShaderResource); else t.ColorToShaderResource(); }
    void ToNonPixel(Dx12OffscreenTarget t)   { if (asyncCl != null) t.ColorTransitionInList(asyncCl, ResourceStates.NonPixelShaderResource); else t.ColorToNonPixelShaderResource(); }
    void ToUav(Dx12OffscreenTarget t)        { if (asyncCl != null) t.ColorTransitionInList(asyncCl, ResourceStates.UnorderedAccess); else t.ColorToUnorderedAccess(); }
    void Copy(Dx12OffscreenTarget dst, Dx12OffscreenTarget src) { if (asyncCl != null) dst.CopyColorFromInList(asyncCl, src); else dst.CopyColorFrom(src); }

    // P7 #1 update-budget dirty tracking: the sun dir/color + light count the cache was last FULLY relit with.
    // A change (or a topology rebuild) → ForceFull this frame so the round-robin budget never starves a light
    // change of latency. NaN sentinel forces a full relight on the first frame.
    Vector3 prevSunDir = new(float.NaN, 0, 0);
    Vector3 prevSunColor;
    float prevLightCount = -1f;

    static readonly bool AuroraProfile = Environment.GetEnvironmentVariable("BALLISTIC_DX12_AURORA_PROFILE") == "1";
    System.Diagnostics.Stopwatch profSw = new();
    void Prof(string tag) { if (AuroraProfile) { profSw.Stop(); Console.WriteLine($"[AuroraProf] {tag} {profSw.Elapsed.TotalMilliseconds:0.00}ms"); profSw.Restart(); } }

    public unsafe void Record(Dx12FrameContext ctx)
    {
        if (AuroraProfile) profSw.Restart();
        // Build/refresh the substrate (shared TLAS + bindless geo + card table + atlases) and log its counts.
        if (!scene.Ensure(ctx))
            return;   // no valid scene AS → nothing to trace (Aurora is HW-RT only; no SSGI fallback)
        Prof("scene.Ensure");

        var sceneAS = ctx.Dxr.SceneAS;
        var rtGeo = ctx.Dxr.RtGeometry;
        if (!rtGeo.Valid) return;

        var gbuffer = ctx.GBuffer;
        var ibl = ctx.Ibl;
        var clusteredLights = ctx.ClusteredLights;
        var target = ctx.SceneColor;

        // ASYNC GI: when armed, the combine adds the PREVIOUS frame's filtered E (indirectFilteredB) into the HDR
        // color NOW, then the trace chain for THIS frame is recorded afterwards (writing indirectFiltered) so it
        // decouples from / overlaps the combine. First async frame has no history → combine is skipped (GI invisible
        // that one frame, accepted). Door off → asyncGi false → legacy single-phase order (combine reads this frame).
        bool asyncGi = AsyncGiArmed(ctx) && !NrdMode();   // NRD owns the temporal; async-GI's 1-frame delay would conflict

        Matrix4x4.Invert(ctx.ViewProj, out Matrix4x4 invVP);

        // Dials: the GlobalIllumination VOLUME (ctx.PostFX) drives them; the BALLISTIC_DX12_AURORA_* env doors
        // override for A/B (EnvF returns the env value when set, else the volume-supplied fallback).
        var fx = ctx.PostFX;
        float intensity = EnvF("BALLISTIC_DX12_AURORA_INTENSITY", fx.AuroraIntensity);
        float rayCount = MathF.Round(EnvF("BALLISTIC_DX12_AURORA_RAYS", fx.AuroraRayCount));
        float maxDist = EnvF("BALLISTIC_DX12_AURORA_DIST", 40f);
        float skyIntensity = EnvF("BALLISTIC_DX12_AURORA_SKY", fx.AuroraSkyIntensity);
        bool useSky = Environment.GetEnvironmentVariable("BALLISTIC_DX12_AURORA_NOSKY") != "1";
        bool useCards = Environment.GetEnvironmentVariable("BALLISTIC_DX12_AURORA_NOCARDS") != "1";

        // QUALITY TIER: probe oct resolution comes from the volume (fx.AuroraProbeOct), env door overrides. If the
        // tier changed octSize since last frame, re-size the octahedral atlas trio (cheap — 3 small textures).
        int wantOct = Math.Clamp((int)EnvF("BALLISTIC_DX12_AURORA_PROBE_OCT", fx.AuroraProbeOct), 4, 16);
        if (wantOct != octSize) { octSize = wantOct; ReallocProbeAtlas(); }

        Vector3 sunDirN = ctx.LightDir.LengthSquared() < 1e-8f ? Vector3.UnitY : Vector3.Normalize(ctx.LightDir);

        // === CARD LIGHTING (P3): light every triangle card into CardRadiance before the trace samples them.
        // 1D dispatch over all scene triangles. Skipped when cards are off (A/B re-shade path). ===
        if (useCards && scene.TotalTriangles > 0)
            LightCards(ctx, sunDirN, clusteredLights, ibl, skyIntensity, useSky);
        Prof("LightCards");

        // ASYNC GI — PHASE A (combine of the PREVIOUS frame's GI), recorded BEFORE this frame's trace chain so the
        // trace overlaps. Skipped on the very first async frame (no history yet). The combine reads indirectFilteredB.
        if (asyncGi && asyncHistoryValid)
        {
            RecordCombine(ctx, gbuffer, target, fx, indirectFilteredB);
            Prof("combine(async-prev)");
        }

        traceCb.Write(new AuroraConstants
        {
            InvViewProj = Matrix4x4.Transpose(invVP),
            ViewProj = Matrix4x4.Transpose(ctx.ViewProj),
            CameraPos = ctx.CamPos, Intensity = intensity,
            TexelSize = new Vector2(1f / indirect.Width, 1f / indirect.Height),
            RayCount = rayCount, FrameIndex = ctx.DeterministicCapture ? 0f : frameCounter,
            NormalBias = EnvF("BALLISTIC_DX12_AURORA_NORMALBIAS", 0.03f), MaxRayDist = maxDist, UseCards = useCards ? 1f : 0f, ScreenSteps = 16f,
            SkyIntensity = skyIntensity, UseSky = useSky ? 1f : 0f,
            // Screen-trace DEFAULT OFF. It contributed a "ghost of another geometry" smudge that's static (not
            // temporal) and GI-only: a screen-trace hit returns the hit pixel's full LIT SceneColor (albedo
            // INCLUDED), so (a) it double-counts albedo vs the RT cards path (which returns albedo-free irradiance
            // the combine multiplies), and (b) on a thin silhouette its thickness window mis-hits the FOREGROUND
            // geometry behind/beside it, painting that surface's colour/shadow onto the edge. The RT trace already
            // owns near+mid+far GI correctly and view-independently, so screen-trace's only role (a cheap contact
            // bounce) isn't worth the artifact. Re-enable with BALLISTIC_DX12_AURORA_SCREEN=1.
            UseScreenTrace = Environment.GetEnvironmentVariable("BALLISTIC_DX12_AURORA_SCREEN") == "1" ? 1f : 0f,
            // Short confident-contact range for the screen trace; mid/far GI is RT (view-independent). The old
            // behaviour let ANY on-screen hit veto RT → view-dependent darkening when the light source panned off.
            ScreenRange = EnvF("BALLISTIC_DX12_AURORA_SCREEN_RANGE", 1.5f),
            // #3 probe temporal accumulation. HistoryValid 0 on the first frame / after a resize → take raw E.
            // ProbeAlpha = this-frame weight in the EMA (lower = smoother + more lag). A deterministic capture KEEPS
            // accumulation (a fixed frame means a fixed, reproducible accumulation over the static camera — and the
            // accumulated result is the CLEAN one we want to measure, not a single noisy frame).
            // CSTrace's INLINE temporal: in PLAY the new common AuroraTemporal pass owns the temporal resolve (real
            // motion vector), so the inline path is OFF to avoid double temporal. But a DETERMINISTIC CAPTURE skips
            // the common pass (golden stability) → keep the inline temporal there so the golden is UNCHANGED (the
            // inline accumulation was what produced the golden SHA). BALLISTIC_DX12_AURORA_INLINE_TEMPORAL=1 forces it on.
            HistoryValid = (probeHistoryValid && (ctx.DeterministicCapture
                            || Environment.GetEnvironmentVariable("BALLISTIC_DX12_AURORA_INLINE_TEMPORAL") == "1")) ? 1f : 0f,
            ProbeAlpha = EnvF("BALLISTIC_DX12_AURORA_PROBE_ALPHA", 0.05f),   // 0.05 = strong temporal accumulation (kills per-ray sparkle); the soft-trust blend handles motion so a low base alpha no longer causes gitgel
            // #4 importance sampling: guarantee a sun-facing ray. DEFAULT OFF — measured on the GI Test scene it
            // did NOT help and slightly HURT at 1 ray (10.8% -> 12.7% hotspot): that scene's dominant indirect is
            // sky-ambient + many point/spot bounces, not the sun, so spending a ray on the sun direction missed the
            // real contributors. Correct importance needs the actual radiance distribution (octahedral probes /
            // ReSTIR) — too big a lift for the marginal gain now that temporal accumulation already cleans the
            // grain. Kept opt-in (BALLISTIC_DX12_AURORA_IMPORTANCE=1) for genuinely sun-dominant scenes.
            ImportanceSampling = EnvF("BALLISTIC_DX12_AURORA_IMPORTANCE", 0f),
            TexelDim = scene.TexelDim,
            PrevViewProj = Matrix4x4.Transpose(ctx.PrevViewProjUnjittered),   // world → prev clip (HLSL column-major)
        });
        sunCb.Write(new AuroraSun
        {
            SunDir = sunDirN, SunBias = 0.03f, SunColor = ctx.LightColor, LightCount = clusteredLights.LightCount,
        });

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Dx12DescriptorHeap bindless = Dx12Backend.BindlessHeap;
        // TLAS is a ROOT SRV (bound below); the table holds t1-t6 + u0 in the reserved tail (so the one bound
        // CBV/SRV/UAV heap serves both the table AND the closest-hit's ResourceDescriptorHeap[] bindless reads).
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(AuroraTableBase + 0), gbuffer.DepthSrvCpu, heapType);     // t1 depth
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(AuroraTableBase + 1), gbuffer.ColorSrvCpu(1), heapType);  // t2 world normal
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(AuroraTableBase + 2), gbuffer.ColorSrvCpu(2), heapType);  // t3 material
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(AuroraTableBase + 3), target.ColorSrvCpu, heapType);      // t4 lit scene color
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(AuroraTableBase + 4), ibl.IrradianceSrv, heapType);       // t5 sky irradiance
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(AuroraTableBase + 5), ibl.PrefilterSrv, heapType);        // t6 sky prefilter
        dev.Device.CreateUnorderedAccessView(indirect.RenderTarget, null, new UnorderedAccessViewDescription
        {
            Format = Dx12OffscreenTarget.HdrFormat, ViewDimension = UnorderedAccessViewDimension.Texture2D,
        }, bindless.Cpu(AuroraTableBase + 6));                                                                      // u0 indirect
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(AuroraTableBase + 7), probeHistory.ColorSrvCpu, heapType);  // t14 ProbeHistory (#3)
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(AuroraTableBase + 8), gbuffer.ColorSrvCpu(4), heapType);    // t15 motion (ghosting reject)

        // The trace reads depth/normal/material/scene-color as SRVs from the COMPUTE (non-pixel) stage, and the
        // scene color must be readable too. Promote: G-buffer to the combined read; scene color to SRV. ALL in
        // one list so state tracking is exact (the RTAO-pass pattern that avoided the split-submit barrier bugs).
        //
        // ASYNC GI cross-queue state ownership: when the trace will be recorded on the COMPUTE queue, the GRAPHICS
        // queue must leave every INPUT it reads in a COMPUTE-legal state BEFORE the hand-off (a compute list cannot
        // reach PIXEL_SHADER_RESOURCE). gbuffer.ToShaderResource() is already the combined PIXEL|NON_PIXEL superset
        // (compute-legal). target (lit scene color, t4 — only read when screen-trace is on) + probeHistory (t14) +
        // probeAtlasHistory (t13, used by the screen-probe path) → NON_PIXEL here on graphics; the compute trace
        // only READS them. Cards (CardRadiance) were already left NonPixelShaderResource by LightCards (above) — a
        // compute-legal state — so the compute trace's root-SRV read is valid with no extra transition.
        bool asyncTrace = asyncGi && dev.AsyncComputeEnabled && dev.FrameOpen && !scene.DirtyThisFrame;
        gbuffer.ToShaderResource();
        if (asyncTrace)
        {
            target.ColorToNonPixelShaderResource();
            probeHistory.ColorToNonPixelShaderResource();
            probeAtlasHistory.ColorToNonPixelShaderResource();   // screen-probe path reads it as t13 (NON_PIXEL)
            // ROOT CAUSE 1 — a COMPUTE list can ONLY express COMMON/UAV/NON_PIXEL/COPY states; it can NEVER write a
            // ResourceBarrier from/to RENDER_TARGET (GBV: "D3D12_RESOURCE_STATES has invalid flags (0x4) for compute
            // command list"). Every offscreen scratch the trace phase touches RESTS in RENDER_TARGET (its ctor state).
            // So the GRAPHICS queue must move them ALL into their compute-legal trace state HERE, pre-hand-off — then
            // the ToUav()/ToShaderRead() calls inside RecordTracePhase (which run on the compute list) become NO-OPS
            // (ColorTransitionInList → TransitionTo is idempotent: state==target writes no barrier). The light-cull
            // pattern, applied to textures: the Direct queue performs the only RENDER_TARGET transitions; the compute
            // queue sees these targets already in UAV/NON_PIXEL and only ever transitions among compute-legal states.
            //   indirect / indirectFiltered / indirectResolved / probeAtlas / probeAtlasFiltered → UAV (trace writes them)
            //   probeAtlasHistory + target + probeHistory → NON_PIXEL (above; trace only reads them)
            // These pre-hand-off transitions are ASYNC-PATH ONLY — the inline/door-off path is untouched (below).
            indirect.ColorToUnorderedAccess();
            indirectFiltered.ColorToUnorderedAccess();
            indirectResolved.ColorToUnorderedAccess();
            probeAtlas.ColorToUnorderedAccess();
            probeAtlasFiltered.ColorToUnorderedAccess();
        }
        else
        {
            target.ColorToShaderResource();
            probeHistory.ColorToShaderResource();   // #3: the trace reads last frame's accumulated probes (table t14)
            indirect.ColorToUnorderedAccess();
        }

        Prof("pre-trace setup");

        // NRD MODE: replace the whole probe-EMA + à-trous + temporal chain with NRD/ReBLUR. The trace writes raw E +
        // packed radiance/hitDist, NRD denoises into indirectFiltered (which combine reads). Falls back to the normal
        // path if NRD can't init. Skipped under a deterministic capture (NRD's history isn't golden-stable single-frame).
        if (NrdMode() && !ctx.DeterministicCapture && EnsureNrd())
        {
            RecordNrd(ctx, sceneAS, rtGeo, clusteredLights, gbuffer);   // writes denoised E into indirectFiltered
            probeHistoryValid = true;   // NRD now has history → CONTINUE accumulation next frame
            Prof("NRD denoise");
            return;   // back to Record(): the legacy combine below reads indirectFiltered (NRD's output)
        }

        // The trace PHASE — per-pixel/screen-probe front end + denoise + temporal + history snapshot. Recorded as
        // one block so it runs EITHER inline on the graphics frame list (door off → byte-identical) OR on the async
        // compute queue (door on → overlaps the post-handoff graphics: Fog/Reflections/Post). RecordAsyncCompute
        // falls back to inline when the infra is off or a frame isn't open.
        if (asyncTrace)
        {
            dev.RecordAsyncCompute(cl =>
            {
                asyncCl = cl;
                try { RecordTracePhase(ctx, sceneAS, rtGeo, clusteredLights, ibl, target, gbuffer, fx,
                                       intensity, maxDist, skyIntensity, useSky, useCards); }
                finally { asyncCl = null; }
            });
        }
        else
        {
            RecordTracePhase(ctx, sceneAS, rtGeo, clusteredLights, ibl, target, gbuffer, fx,
                             intensity, maxDist, skyIntensity, useSky, useCards);
        }
        Prof("TRACE/gather");

        if (asyncGi)
        {
            // ASYNC GI — PHASE B done: the trace chain above wrote THIS frame's filtered E into indirectFiltered.
            // The combine for this frame already ran (Phase A, reading last frame's buffer), so DON'T combine again
            // here. Swap the two filtered buffers so this frame's freshly-written E becomes next frame's "previous"
            // the combine reads, and the now-stale B buffer becomes next frame's trace write target. Mark history
            // valid so the next frame's Phase A combine fires.
            (indirectFiltered, indirectFilteredB) = (indirectFilteredB, indirectFiltered);
            asyncHistoryValid = true;
        }
        else
        {
            // LEGACY single-phase order: combine THIS frame's freshly-traced E now (byte-identical to before).
            RecordCombine(ctx, gbuffer, target, fx, indirectFiltered);
        }

        Prof("combine");
        // Swap the cache ping-pong: this frame's written cache becomes next frame's "previous" (EMA + bounce
        // source). Only when cards actually ran this frame (else the read/write buffers didn't advance).
        if (useCards && scene.TotalTriangles > 0)
            scene.SwapCache();
        frameCounter++;
    }

    // TRACE PHASE — the heavy GI work: front-end trace (screen-probe place/trace/integrate OR per-pixel CSTrace),
    // spatial denoise, motion-vector temporal resolve, and the history snapshot that writes `indirectFiltered`. The
    // INPUTS are already promoted to the right state by the caller (gbuffer combined-read; on the async path target/
    // probeHistory/probeAtlasHistory in NON_PIXEL, on graphics, before the hand-off). Every GPU command goes through
    // Emit()/Copy()/ToShaderRead()/ToUav() so the SAME code records either inline on the graphics frame list (door
    // off → byte-identical to the legacy single-phase order, each helper expanding to the exact ExecuteSync it
    // replaced) or into ONE compute command list (door on, `asyncCl` set → overlaps the post-hand-off graphics).
    unsafe void RecordTracePhase(Dx12FrameContext ctx, Dx12SceneAS sceneAS, Dx12RtGeometry rtGeo,
                                 Dx12ClusteredLights clusteredLights, Dx12IblBaker ibl, Dx12OffscreenTarget target,
                                 Dx12GBuffer gbuffer, PostProcessSettings fx, float intensity, float maxDist,
                                 float skyIntensity, bool useSky, bool useCards)
    {
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Dx12DescriptorHeap bindless = Dx12Backend.BindlessHeap;

        if (WantScreenProbe(ctx))
        {
            // Sıra 1: SCREEN-PROBE front end fills `indirect` (place → trace → integrate). Same E contract.
            TraceScreenProbe(ctx, sceneAS, rtGeo, clusteredLights, intensity, maxDist, skyIntensity, useSky, useCards);
        }
        else
        {
            Emit(cl =>
            {
                cl.SetDescriptorHeaps(bindless.Heap);
                cl.SetComputeRootSignature(traceRootSig);
                cl.SetPipelineState(tracePso);
                cl.SetComputeRootConstantBufferView(0, traceCb.Gpu);
                cl.SetComputeRootConstantBufferView(1, sunCb.Gpu);
                cl.SetComputeRootShaderResourceView(2, sceneAS.TlasAddress);                  // t0 TLAS (root SRV)
                cl.SetComputeRootDescriptorTable(3, bindless.Gpu(AuroraTableBase));            // t1-t6 + u0
                cl.SetComputeRootShaderResourceView(4, ctx.GpuDriven.MaterialsGpuAddress);    // t7 GpuMaterials
                cl.SetComputeRootShaderResourceView(5, rtGeo.InstancesGpuAddress);            // t8 RtInstance[]
                cl.SetComputeRootShaderResourceView(6, clusteredLights.LightBufGpuAddress);   // t9 punctual lights
                cl.SetComputeRootShaderResourceView(7, scene.CardRadianceWriteGpu);           // t10 CardRadiance (this frame's stable cache)
                cl.SetComputeRootShaderResourceView(8, scene.InstanceMetaGpuAddress);         // t11 InstanceMeta
                cl.SetComputeRootShaderResourceView(9, scene.TriToClusterGpuAddress);         // t12 TriToCluster (#2A)
                cl.SetComputeRootShaderResourceView(10, scene.ClusterCardsGpuAddress);    // t13 ClusterCards (Sıra 5)
                cl.Dispatch((uint)((indirect.Width + 7) / 8), (uint)((indirect.Height + 7) / 8), 1);
            });
        }
        ToShaderRead(indirect);

        // === COMMON MOTION-VECTOR TEMPORAL RESOLVE (AuroraTemporal) ===
        bool temporalOn = Environment.GetEnvironmentVariable("BALLISTIC_DX12_AURORA_NOTEMPORAL") != "1"
                          && !ctx.DeterministicCapture;   // deterministic capture: skip (single frame, fresh) for golden stability
        bool historyMissThisFrame = !probeHistoryValid;

        // === SPATIAL DENOISE FIRST (the flicker fix): à-trous blur of the per-pixel grain, BEFORE the temporal
        // resolve, so combine + history read the SAME denoised+resolved signal. ===
        bool denoise = Environment.GetEnvironmentVariable("BALLISTIC_DX12_AURORA_NODENOISE") != "1" && fx.AuroraDenoisePasses > 0;
        int dnPasses = denoise ? Math.Clamp((int)EnvF("BALLISTIC_DX12_AURORA_DENOISE_PASSES", fx.AuroraDenoisePasses), 1, MaxDenoisePasses) : 1;
        bool adaptiveDenoise = denoise && Environment.GetEnvironmentVariable("BALLISTIC_DX12_AURORA_DENOISE_PASSES") == null
                               && !ctx.DeterministicCapture;
        if (adaptiveDenoise && historyMissThisFrame)
            dnPasses = Math.Clamp(Math.Max(dnPasses, 3), 1, MaxDenoisePasses);
        // Denoise the FRESH trace E (indirect) into indirectFiltered, ping-ponging indirect↔indirectFiltered. The
        // final denoised result is left in `indirect` (so the temporal pass below reads the denoised fresh E as t0).
        Dx12OffscreenTarget src = indirect, dst = indirectFiltered;
        denoiseSrv.Reset();   // ONCE per frame — each pass takes a DISTINCT 4-descriptor range (no cross-pass alias)
        for (int pass = 0; pass < dnPasses; pass++)
        {
            *(DenoiseConstants*)denoiseCbMappedArr[pass] = new DenoiseConstants
            {
                Texel = new Vector2(1f / indirect.Width, 1f / indirect.Height),
                Step = denoise ? (1 << pass) : 1f, Enabled = denoise ? 1f : 0f,
            };
            int db = denoiseSrv.AllocateRange(4);
            dev.Device.CopyDescriptorsSimple(1, denoiseSrv.Cpu(db + 0), src.ColorSrvCpu, heapType);           // t0 E in
            dev.Device.CopyDescriptorsSimple(1, denoiseSrv.Cpu(db + 1), gbuffer.DepthSrvCpu, heapType);       // t1 depth
            dev.Device.CopyDescriptorsSimple(1, denoiseSrv.Cpu(db + 2), gbuffer.ColorSrvCpu(1), heapType);    // t2 normal
            dev.Device.CreateUnorderedAccessView(dst.RenderTarget, null, new UnorderedAccessViewDescription
            {
                Format = Dx12OffscreenTarget.HdrFormat, ViewDimension = UnorderedAccessViewDimension.Texture2D,
            }, denoiseSrv.Cpu(db + 3));                                                                        // u0 E out
            ToUav(dst);
            ulong passCbAddr = denoiseCbs[pass].GPUVirtualAddress + (ulong)DenoiseCbOffset;
            Emit(cl =>
            {
                cl.SetDescriptorHeaps(denoiseSrv.Heap);
                cl.SetComputeRootSignature(denoiseRootSig);
                cl.SetPipelineState(denoisePso);
                cl.SetComputeRootConstantBufferView(0, passCbAddr);
                cl.SetComputeRootDescriptorTable(1, denoiseSrv.Gpu(db));
                cl.Dispatch((uint)((indirect.Width + 7) / 8), (uint)((indirect.Height + 7) / 8), 1);
            });
            ToShaderRead(dst);
            (src, dst) = (dst, src);
        }
        // Land the denoised result back in `indirect` (the temporal pass's t0 fresh-E input). `src` holds the last
        // written buffer; copy it into indirect when that isn't already indirect.
        if (!ReferenceEquals(src, indirect))
            Copy(indirect, src);
        ToShaderRead(indirect);
        Prof("denoise");

        // === COMMON MOTION-VECTOR TEMPORAL RESOLVE (AuroraTemporal) — AFTER the denoise ===
        if (temporalOn)
        {
            tempCb.Write(new TemporalConstants
            {
                Texel = new Vector2(1f / indirect.Width, 1f / indirect.Height),
                HistoryValid = probeHistoryValid ? 1f : 0f,
                Alpha = EnvF("BALLISTIC_DX12_AURORA_TEMPORAL_ALPHA", 0.1f),
                W = (uint)indirect.Width, H = (uint)indirect.Height,
                Pad0 = Environment.GetEnvironmentVariable("BALLISTIC_DX12_AURORA_TEMPORAL_NOMOTION") == "1" ? 1f : 0f,
            });
            ToShaderRead(probeHistory);
            ToUav(indirectResolved);
            tempSrv.Reset();
            int tb = tempSrv.AllocateRange(5);
            dev.Device.CopyDescriptorsSimple(1, tempSrv.Cpu(tb + 0), indirect.ColorSrvCpu, heapType);          // t0 denoised fresh E
            dev.Device.CopyDescriptorsSimple(1, tempSrv.Cpu(tb + 1), probeHistory.ColorSrvCpu, heapType);      // t1 history
            dev.Device.CopyDescriptorsSimple(1, tempSrv.Cpu(tb + 2), gbuffer.DepthSrvCpu, heapType);           // t2 depth
            dev.Device.CopyDescriptorsSimple(1, tempSrv.Cpu(tb + 3), gbuffer.ColorSrvCpu(4), heapType);        // t3 motion (RT4)
            dev.Device.CreateUnorderedAccessView(indirectResolved.RenderTarget, null, new UnorderedAccessViewDescription
            {
                Format = Dx12OffscreenTarget.HdrFormat, ViewDimension = UnorderedAccessViewDimension.Texture2D,
            }, tempSrv.Cpu(tb + 4));                                                                            // u0 resolved
            ulong tcb = tempCb.Gpu;
            Emit(cl =>
            {
                cl.SetDescriptorHeaps(tempSrv.Heap);
                cl.SetComputeRootSignature(tempRootSig);
                cl.SetPipelineState(tempPso);
                cl.SetComputeRootConstantBufferView(0, tcb);
                cl.SetComputeRootDescriptorTable(1, tempSrv.Gpu(tb));
                cl.Dispatch((uint)((indirect.Width + 7) / 8), (uint)((indirect.Height + 7) / 8), 1);
            });
            ToShaderRead(indirectResolved);
            // resolved E → the combine reads it (via indirectFiltered) AND it becomes next frame's history.
            Copy(indirectFiltered, indirectResolved);
            ToShaderRead(indirectFiltered);
            Copy(probeHistory, indirectResolved);
            ToShaderRead(probeHistory);
            probeHistoryValid = true;
        }
        else
        {
            // No temporal (deterministic/off): the combine reads the denoised E; snapshot it as history too.
            Copy(indirectFiltered, indirect);
            ToShaderRead(indirectFiltered);
            Copy(probeHistory, indirect);
            ToShaderRead(probeHistory);
            probeHistoryValid = true;
        }
        Prof("temporal");
    }

    // COMBINE: add E*albedo*ao/PI from `eBuffer` (a filtered indirect-irradiance target) directly into the HDR
    // scene color via an additive (One/One) fullscreen PSO — no scratch target needed. The deferred pass already
    // suppressed its IBL diffuse ambient (ctx.AuroraActiveThisFrame → UseIBLDiffuse=0), so this adds Aurora's diffuse
    // indirect without double-count. BALLISTIC_DX12_AURORA_DEBUG=1 swaps to an OPAQUE-replace PSO showing raw E.
    // `eBuffer` = this frame's trace output (legacy/sync path) OR last frame's (async-GI path) — same shape either way.
    unsafe void RecordCombine(Dx12FrameContext ctx, Dx12GBuffer gbuffer, Dx12OffscreenTarget target,
                              PostProcessSettings fx, Dx12OffscreenTarget eBuffer)
    {
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        gbuffer.ToShaderResource();
        eBuffer.ColorToShaderResource();   // the combine reads E as a pixel-shader SRV (idempotent if already SRV)
        // GTAO into the GI combine at the AmbientOcclusion volume's strength (env override _LUMEN_AO). The GTAO
        // buffer is ctx.AoResult when AO is actually rendered this frame; else a valid fallback + AoStrength 0
        // (so the fallback's contents never affect the output). This is what makes the AmbientOcclusion override
        // drive contact detail in the GI; the RT trace already has macro occlusion so the default strength is
        // partial (no double-darkening of corners).
        // GTAO is NOT mixed into the GI by default. It's a SCREEN-SPACE term, so under camera motion it dragged a
        // dark "ghost of nearby geometry" smudge onto every surface (static per view, GI-only — the reported bug),
        // and the Aurora RT trace ALREADY carries macro occlusion (rays that don't escape find less light), so GTAO
        // on top double-darkened corners anyway. Default strength 0 (the volume's AuroraAoStrength / env can opt it
        // back in for a scene that specifically wants the extra contact term and tolerates the screen-space cost).
        bool aoOn = ctx.Doors.Ssao && fx.SSAOEnabled;
        float aoStrength = aoOn ? EnvF("BALLISTIC_DX12_AURORA_AO", 0f) : 0f;
        combineCb.Write(new CombineConstants
        {
            AoStrength = aoStrength,
            IndirectTexel = new Vector2(1f / eBuffer.Width, 1f / eBuffer.Height),   // half-res texel for the upsample
        });
        combineSrv.Reset();
        int cb = combineSrv.AllocateRange(5);
        dev.Device.CopyDescriptorsSimple(1, combineSrv.Cpu(cb + 0), eBuffer.ColorSrvCpu, heapType);           // t0 E (denoised)
        dev.Device.CopyDescriptorsSimple(1, combineSrv.Cpu(cb + 1), gbuffer.ColorSrvCpu(0), heapType);        // t1 albedo
        dev.Device.CopyDescriptorsSimple(1, combineSrv.Cpu(cb + 2), gbuffer.ColorSrvCpu(2), heapType);        // t2 material (baked ao)
        dev.Device.CopyDescriptorsSimple(1, combineSrv.Cpu(cb + 3), gbuffer.DepthSrvCpu, heapType);           // t3 depth
        dev.Device.CopyDescriptorsSimple(1, combineSrv.Cpu(cb + 4), aoOn ? ctx.AoResult : gbuffer.DepthSrvCpu, heapType); // t4 GTAO
        bool debugE = Environment.GetEnvironmentVariable("BALLISTIC_DX12_AURORA_DEBUG") == "1";
        ID3D12PipelineState pso = debugE ? combineDebugPso : combinePso;
        target.RenderColorOnly(cl =>
        {
            cl.SetGraphicsRootSignature(combineRootSig);
            cl.SetPipelineState(pso);                  // additive One/One blend (or opaque replace when debugE)
            cl.SetDescriptorHeaps(combineSrv.Heap);
            cl.SetGraphicsRootConstantBufferView(0, combineCb.Gpu);
            cl.SetGraphicsRootDescriptorTable(1, combineSrv.Gpu(cb));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
    }

    // P3 card lighting: 1D dispatch over all scene triangles, writing each triangle's lit first-bounce radiance
    // into scene.CardRadiance. Reads the shared TLAS (shadow rays) + bindless geo/material + the per-instance
    // meta. The trace then samples these cards on RT hits (no per-hit relighting).
    unsafe void LightCards(Dx12FrameContext ctx, Vector3 sunDir, Dx12ClusteredLights clusteredLights,
                           Dx12IblBaker ibl, float skyIntensity, bool useSky)
    {
        var sceneAS = ctx.Dxr.SceneAS;
        float emaAlpha = EnvF("BALLISTIC_DX12_AURORA_EMA", 0.05f);         // 0.05 (was 0.1): smaller per-relight radiance step so a
        // round-robin card update is a gentler jump → less visible per-cluster flash. Slower to react to a real light
        // change, but ForceFull (sun/light/topology dirty) bypasses the EMA entirely so genuine changes stay instant.
        bool bounce = Environment.GetEnvironmentVariable("BALLISTIC_DX12_AURORA_NOBOUNCE") != "1" && ctx.PostFX.AuroraMultiBounce;

        // === P7 #1 UPDATE BUDGET ===
        // Re-light only a round-robin slice of records each frame instead of the whole scene. stride = how many
        // frames a full sweep takes; a record relights every `stride`-th frame. budget = target records/frame
        // (the door BALLISTIC_DX12_AURORA_BUDGET overrides; 0 = unlimited → stride 1 = old behaviour). The EMA
        // makes a strided update visually identical to a per-frame one for a STATIC light. Small scenes
        // (tris ≤ budget) get stride 1 automatically (no change). Determinism: a deterministic capture forces
        // stride 1 (a strided cache depends on frame count → not byte-reproducible).
        int budget = (int)EnvF("BALLISTIC_DX12_AURORA_BUDGET", ctx.PostFX.AuroraCardBudget);
        int tris = scene.RecordCount;   // budget now counts RECORDS (clusters), the card-light dispatch unit
        uint stride = 1u;
        // A deterministic capture renders a FIXED frame, so the round-robin phase (FrameIndex % stride) is itself
        // deterministic → byte-identical across runs. Hence budget is safe under DeterministicCapture (it does NOT
        // disable it like the EMA does), so `bal perf` measures the real budgeted cost.
        if (budget > 0 && tris > budget)
            stride = (uint)Math.Min(4, (tris + budget - 1) / budget);   // cap at 4 → ≤4-frame relight period (was 8: an 8-frame
        // window meant each cluster held stale 8 frames then STEPPED its radiance at once — a ~7-8 Hz per-cluster flash
        // (the user's "saniyede bir parlama"). Halving the cap halves both the stale window AND the per-step jump.

        // ForceFull this frame when the light state changed (or topology rebuilt) so the budget never delays a
        // light change. Compared against the values the cache was last fully relit with.
        bool lightChanged = float.IsNaN(prevSunDir.X)
            || Vector3.DistanceSquared(prevSunDir, sunDir) > 1e-8f
            || Vector3.DistanceSquared(prevSunColor, ctx.LightColor) > 1e-6f
            || prevLightCount != clusteredLights.LightCount
            || scene.DirtyThisFrame;
        uint forceFull = lightChanged ? 1u : 0u;
        if (lightChanged) { prevSunDir = sunDir; prevSunColor = ctx.LightColor; prevLightCount = clusteredLights.LightCount; }

        // P7 #1b PRIORITY budget: spend the same average records/frame as the stride, but weighted by staleness ×
        // camera proximity so near cards react fast and far ones update lazily. OFF under a deterministic capture
        // (its hash×frame gate is not byte-reproducible — same reason the EMA is disabled there) and when the
        // budget is unlimited (stride 1 already updates everything). Env door BALLISTIC_DX12_AURORA_PRIORITY=0 forces
        // the legacy flat round-robin. PriorityScale sets the mean due-rate to ≈ budget/recordCount: at steady
        // state a record's staleness averages recordCount/budget, and (0.25+nearW) averages ~0.6, so scaling by
        // (recordCount/budget)·0.6 lands the mean probability near budget/recordCount.
        bool priorityOn = budget > 0 && tris > budget && !ctx.DeterministicCapture
                          && Environment.GetEnvironmentVariable("BALLISTIC_DX12_AURORA_PRIORITY") != "0";
        float priorityScale = priorityOn ? Math.Max(1f, (float)tris / budget * 0.85f) : 1f;

        // FAZ 3d — build the emissive-triangle area-light list (NEE). Cached by an instance+emissive stamp, so a
        // static scene builds it once. Door BALLISTIC_DX12_AURORA_NEE (default ON); empty list → NEE inert.
        bool neeOn = Environment.GetEnvironmentVariable("BALLISTIC_DX12_AURORA_NEE") != "0";
        if (neeOn) emissiveLights.Ensure(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection);
        float neeCount = (neeOn && emissiveLights.Valid) ? emissiveLights.Count : 0f;
        float neeIntensity = EnvF("BALLISTIC_DX12_AURORA_NEE_INTENSITY", 1f);

        cardCb.Write(new AuroraCardConstants
        {
            SunDir = sunDir, SunBias = 0.03f, SunColor = ctx.LightColor, LightCount = clusteredLights.LightCount,
            InstanceCount = (uint)scene.InstanceCount, TotalTris = (uint)scene.RecordCount,   // #2A: dispatch bound = record count
            SkyIntensity = skyIntensity, UseSky = useSky ? 1f : 0f, SkyVisRays = 4f,
            EmaAlpha = emaAlpha, BounceRays = bounce ? 4f : 0f,
            HistoryValid = (scene.HistoryValid && !ctx.DeterministicCapture) ? 1f : 0f,
            FrameIndex = (uint)frameCounter, UpdateStride = stride, ForceFull = forceFull,
            TexelDim = (uint)scene.TexelDim,
            CameraPos = ctx.CamPos, PriorityScale = priorityScale,
            PriorityNearDist = EnvF("BALLISTIC_DX12_AURORA_PRIORITY_NEAR", 12f), UsePriority = priorityOn ? 1f : 0f,
            EmissiveCount = neeCount, NeeIntensity = neeIntensity,
        });

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Dx12DescriptorHeap bindless = Dx12Backend.BindlessHeap;
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(CardSkyTableBase + 0), ibl.IrradianceSrv, heapType);   // t1 sky cube

        // Ping-pong: write THIS frame's buffer (UAV), read the PREVIOUS frame's (non-pixel SRV) for the EMA +
        // 2nd bounce. Transition each to the needed state; after the dispatch bring the WRITE buffer to non-
        // pixel SRV too (the trace reads it as a root SRV). The read buffer stays SRV for next frame's swap.
        ID3D12Resource cardW = scene.CardRadianceWrite;
        ID3D12Resource cardR = scene.CardRadianceRead;
        ID3D12Resource ageBuf = scene.LastUpdated;   // P7 #1 per-record age (read+write UAV)
        dev.ExecuteSync(cl =>
        {
            if (scene.StateOf(cardW) != ResourceStates.UnorderedAccess)
                cl.ResourceBarrierTransition(cardW, scene.StateOf(cardW), ResourceStates.UnorderedAccess);
            if (scene.StateOf(cardR) != ResourceStates.NonPixelShaderResource)
                cl.ResourceBarrierTransition(cardR, scene.StateOf(cardR), ResourceStates.NonPixelShaderResource);
            if (ageBuf != null && scene.LastUpdatedState != ResourceStates.UnorderedAccess)
                cl.ResourceBarrierTransition(ageBuf, scene.LastUpdatedState, ResourceStates.UnorderedAccess);
            cl.SetDescriptorHeaps(bindless.Heap);
            cl.SetComputeRootSignature(cardRootSig);
            cl.SetPipelineState(cardPso);
            cl.SetComputeRootConstantBufferView(0, cardCb.Gpu);
            cl.SetComputeRootShaderResourceView(1, sceneAS.TlasAddress);                 // t0 TLAS
            cl.SetComputeRootUnorderedAccessView(2, scene.CardRadianceWriteGpu);         // u0 CardRadiance (write)
            cl.SetComputeRootDescriptorTable(3, bindless.Gpu(CardSkyTableBase));         // t1 sky cube
            cl.SetComputeRootShaderResourceView(4, scene.InstanceMetaGpuAddress);        // t2 AuroraInstanceMeta
            cl.SetComputeRootShaderResourceView(5, ctx.Dxr.RtGeometry.InstancesGpuAddress); // t3 RtInstance[]
            cl.SetComputeRootShaderResourceView(6, ctx.GpuDriven.MaterialsGpuAddress);   // t4 GpuMaterials
            cl.SetComputeRootShaderResourceView(7, clusteredLights.LightBufGpuAddress);  // t5 Lights
            cl.SetComputeRootShaderResourceView(8, scene.CardRadianceReadGpu);           // t6 PrevCard (read)
            cl.SetComputeRootUnorderedAccessView(9, scene.LastUpdatedGpu);               // u1 LastUpdated (age)
            cl.SetComputeRootShaderResourceView(10, scene.TriToClusterGpuAddress);       // t7 TriToCluster
            cl.SetComputeRootShaderResourceView(11, scene.ClusterToTriGpuAddress);       // t8 ClusterToTri
            cl.SetComputeRootShaderResourceView(12, scene.ClusterCardsGpuAddress);       // t9 ClusterCards (Sıra 5)
            cl.SetComputeRootShaderResourceView(13, neeCount > 0f ? emissiveLights.GpuAddress : clusteredLights.LightBufGpuAddress);  // t10 EmissiveLights (valid filler when empty)
            cl.Dispatch((uint)((scene.RecordCount + 63) / 64), 1, 1);                     // #2A: one thread per record
            cl.ResourceBarrierTransition(cardW, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
        });
        scene.SetState(cardW, ResourceStates.NonPixelShaderResource);
        if (ageBuf != null) scene.SetLastUpdatedState(ResourceStates.UnorderedAccess);   // stays UAV (read+write each frame)
        scene.SetState(cardR, ResourceStates.NonPixelShaderResource);
    }

    // Sıra 1 — SCREEN-PROBE front end. Fills `indirect` (full-res E) via place → trace → integrate, replacing the
    // per-pixel CSTrace. Reuses the trace CB's already-set dials (intensity/dist/sky/cards passed in). The G-buffer
    // + scene color + probeHistory are already promoted to SRV by the caller; `indirect` is already UAV.
    unsafe void TraceScreenProbe(Dx12FrameContext ctx, Dx12SceneAS sceneAS, Dx12RtGeometry rtGeo,
                                 Dx12ClusteredLights clusteredLights, float intensity, float maxDist,
                                 float skyIntensity, bool useSky, bool useCards)
    {
        var gbuffer = ctx.GBuffer;
        var ibl = ctx.Ibl;
        var target = ctx.SceneColor;
        Matrix4x4.Invert(ctx.ViewProj, out Matrix4x4 invVP);
        Vector3 sunDirN = ctx.LightDir.LengthSquared() < 1e-8f ? Vector3.UnitY : Vector3.Normalize(ctx.LightDir);

        spProbeCb.Write(new ProbeConstants
        {
            InvViewProj = Matrix4x4.Transpose(invVP),
            ViewProj = Matrix4x4.Transpose(ctx.ViewProj),
            CameraPos = ctx.CamPos, Intensity = intensity,
            FullTexel = new Vector2(1f / indirect.Width, 1f / indirect.Height),
            RayCount = octSize * octSize, FrameIndex = ctx.DeterministicCapture ? 0f : frameCounter,
            NormalBias = 0.03f, MaxRayDist = maxDist, UseCards = useCards ? 1f : 0f, ScreenSteps = 16f,
            SkyIntensity = skyIntensity, UseSky = useSky ? 1f : 0f,
            UseScreenTrace = Environment.GetEnvironmentVariable("BALLISTIC_DX12_AURORA_NOSCREEN") == "1" ? 0f : 1f,
            ScreenRange = EnvF("BALLISTIC_DX12_AURORA_SCREEN_RANGE", 1.5f),
            FalloffDist = EnvF("BALLISTIC_DX12_AURORA_FALLOFF", 12f),
            UseSH = Environment.GetEnvironmentVariable("BALLISTIC_DX12_AURORA_PROBE_NOSH") != "1" ? 1f : 0f,
            ProbeStride = probeStride, OctSize = octSize,
            ProbesX = (uint)probesX, ProbesY = (uint)probesY,
            FullW = (uint)indirect.Width, FullH = (uint)indirect.Height,
            // Sıra 3 temporal accumulation. A deterministic capture KEEPS accumulation (fixed frame → fixed,
            // reproducible accumulation over the static camera — the converged result is what we measure).
            HistoryValid = spHistoryValid ? 1f : 0f,
            ProbeEma = EnvF("BALLISTIC_DX12_AURORA_PROBE_EMA", 0.1f),   // this-frame weight; low = strong accumulation
            TexelDim = scene.TexelDim,
            SpPad1 = EnvF("BALLISTIC_DX12_AURORA_PROBE_FILTER_RADIUS", 2f),   // probe-space spatial filter radius (blob fix)
            // Variance-guided adaptive ray. OFF under deterministic capture (history+frame-phased → would shift the
            // golden) so paused captures stay byte-identical; ON in live play, killable via the env door.
            AdaptiveRays = (Environment.GetEnvironmentVariable("BALLISTIC_DX12_AURORA_PROBE_ADAPTIVE") != "0"
                            && !ctx.DeterministicCapture) ? 1f : 0f,
            AdaptiveStride = EnvF("BALLISTIC_DX12_AURORA_PROBE_ADAPTIVE_STRIDE", 3f),
            AdaptiveVar = EnvF("BALLISTIC_DX12_AURORA_PROBE_ADAPTIVE_VAR", 0.06f),
        });
        spSunCb.Write(new AuroraSun
        {
            SunDir = sunDirN, SunBias = 0.03f, SunColor = ctx.LightColor, LightCount = clusteredLights.LightCount,
        });

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Dx12DescriptorHeap bindless = Dx12Backend.BindlessHeap;
        // PERF: write the screen-probe table descriptors ONLY when a source resource handle changed (resize / scene
        // swap), not every frame. Stamp = the source CPU-descriptor pointers. The bindless tail slots persist, so a
        // cached frame reuses last write — byte-identical output, ~9 driver calls/frame eliminated.
        long descStamp = (long)gbuffer.DepthSrvCpu.Ptr ^ ((long)gbuffer.ColorSrvCpu(1).Ptr << 1)
            ^ ((long)target.ColorSrvCpu.Ptr << 2) ^ ((long)ibl.IrradianceSrv.Ptr << 3)
            ^ ((long)probeAtlas.ColorSrvCpu.Ptr << 4) ^ ((long)indirect.ColorSrvCpu.Ptr << 5)
            ^ ((long)probeAtlasFiltered.ColorSrvCpu.Ptr << 6) ^ ((long)probeAtlasHistory.ColorSrvCpu.Ptr << 7);
        if (descStamp != spDescStamp)
        {
            spDescStamp = descStamp;
            dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(SpTableBase + 0), gbuffer.DepthSrvCpu, heapType);     // t1 depth
            dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(SpTableBase + 1), gbuffer.ColorSrvCpu(1), heapType);  // t2 normal
            dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(SpTableBase + 2), gbuffer.ColorSrvCpu(2), heapType);  // t3 material
            dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(SpTableBase + 3), target.ColorSrvCpu, heapType);      // t4 lit scene color
            dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(SpTableBase + 4), ibl.IrradianceSrv, heapType);       // t5 sky irradiance
            dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(SpTableBase + 5), ibl.PrefilterSrv, heapType);        // t6 sky prefilter
            dev.Device.CreateUnorderedAccessView(probeAtlas.RenderTarget, null, new UnorderedAccessViewDescription
            {
                Format = Dx12OffscreenTarget.HdrFormat, ViewDimension = UnorderedAccessViewDimension.Texture2D,
            }, bindless.Cpu(SpTableBase + 6));                                                                      // u1 probe atlas
            dev.Device.CreateUnorderedAccessView(indirect.RenderTarget, null, new UnorderedAccessViewDescription
            {
                Format = Dx12OffscreenTarget.HdrFormat, ViewDimension = UnorderedAccessViewDimension.Texture2D,
            }, bindless.Cpu(SpTableBase + 7));                                                                      // u2 indirect
            dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(SpTableBase + 8), probeAtlasHistory.ColorSrvCpu, heapType); // t13 atlas history
            dev.Device.CreateUnorderedAccessView(probeAtlasFiltered.RenderTarget, null, new UnorderedAccessViewDescription
            {
                Format = Dx12OffscreenTarget.HdrFormat, ViewDimension = UnorderedAccessViewDimension.Texture2D,
            }, bindless.Cpu(SpTableBase + 9));                                                                      // u3 probe atlas FILTERED (blob fix)
        }

        ToUav(probeAtlas);
        ToUav(probeAtlasFiltered);

        void SetCommonRoots(ID3D12GraphicsCommandList cl)
        {
            cl.SetComputeRootConstantBufferView(0, spProbeCb.Gpu);
            cl.SetComputeRootConstantBufferView(1, spSunCb.Gpu);
            cl.SetComputeRootShaderResourceView(2, sceneAS.TlasAddress);                  // t0 TLAS (root SRV)
            cl.SetComputeRootDescriptorTable(3, bindless.Gpu(SpTableBase));               // t1-t6 + u1 atlas
            cl.SetComputeRootShaderResourceView(4, ctx.GpuDriven.MaterialsGpuAddress);    // t7 GpuMaterials
            cl.SetComputeRootShaderResourceView(5, rtGeo.InstancesGpuAddress);            // t8 RtInstance[]
            cl.SetComputeRootShaderResourceView(6, clusteredLights.LightBufGpuAddress);   // t9 lights
            cl.SetComputeRootShaderResourceView(7, scene.CardRadianceWriteGpu);           // t10 CardRadiance
            cl.SetComputeRootShaderResourceView(8, scene.InstanceMetaGpuAddress);         // t11 InstanceMeta
            cl.SetComputeRootShaderResourceView(9, scene.TriToClusterGpuAddress);         // t12 TriToCluster
            cl.SetComputeRootUnorderedAccessView(10, probeHeaders.GPUVirtualAddress);     // u0 ProbeHeaders (root UAV — buffer)
            cl.SetComputeRootShaderResourceView(12, probeHeadersPrev.GPUVirtualAddress);  // t16 prev headers (root SRV — buffer)
            cl.SetComputeRootShaderResourceView(13, scene.ClusterCardsGpuAddress);        // t17 ClusterCards (Sıra 5)
            cl.SetComputeRootUnorderedAccessView(14, probeSH.GPUVirtualAddress);          // u4 ProbeSH (root UAV — buffer, SH cache)
        }

        // Root slot 11 table = u2 indirect (SpTableBase+7) + t13 atlas history (SpTableBase+8), bound from the
        // SAME bindless heap as slot 3. The descriptors are written ONCE above (not per-dispatch) → no
        // StaticDescriptorInvalidDescriptorChange; one SetDescriptorHeaps(bindless) serves all tables → no
        // SetDescriptorTableInvalid.
        //
        // UAV HAZARDS: the front-end is a RAW chain on shared UAV buffers/textures —
        //   Place(w probeHeaders) → Trace(r probeHeaders, w probeAtlas) → Filter(r probeAtlas, w probeAtlasFiltered)
        //   → SH(r probeAtlasFiltered, w probeSH) → Integrate(r probeAtlasFiltered + probeSH).
        // Under P0a these dispatches record into the SAME open frame list with NO submit/WaitForGpu between them
        // (Dx12Device.ExecuteSync: frame thread just appends), so each read-after-write needs an explicit UAV
        // barrier — there is no implicit per-submit serialisation any more. State stays UNORDERED_ACCESS across the
        // chain (so no TransitionTo fires), which is exactly why a UAV barrier, not a transition, is required.
        var slot11 = bindless.Gpu(SpTableBase + 7);

        // PLACE — writes probeHeaders (root UAV). indirect + atlas already UAV.
        Emit(cl =>
        {
            cl.SetDescriptorHeaps(bindless.Heap);
            cl.SetComputeRootSignature(spRootSig);
            cl.SetPipelineState(spPlacePso);
            SetCommonRoots(cl);
            cl.SetComputeRootDescriptorTable(11, slot11);
            cl.Dispatch((uint)((probesX + 7) / 8), (uint)((probesY + 7) / 8), 1);
            cl.ResourceBarrierUnorderedAccessView(probeHeaders);   // Place(w) → Trace(r) probeHeaders
        });

        // TRACE — reads the previous accumulated atlas (t13) from a COMPUTE shader → NON_PIXEL state (not the
        // default PIXEL from ColorToShaderResource; that PIXEL/NON_PIXEL mismatch was a GBV InvalidSubresourceState).
        // On the async path the caller already left probeAtlasHistory NON_PIXEL on the GRAPHICS queue before the
        // hand-off (a compute list could not transition from PIXEL) — ToNonPixel is then idempotent (no barrier).
        ToNonPixel(probeAtlasHistory);
        Emit(cl =>
        {
            cl.SetDescriptorHeaps(bindless.Heap);
            cl.SetComputeRootSignature(spRootSig);
            cl.SetPipelineState(spTracePso);
            SetCommonRoots(cl);
            cl.SetComputeRootDescriptorTable(11, slot11);
            cl.Dispatch((uint)((probesX * octSize + 7) / 8), (uint)((probesY * octSize + 7) / 8), 1);
            cl.ResourceBarrierUnorderedAccessView(probeAtlas.RenderTarget);   // Trace(w) → snapshot-copy + Filter(r) probeAtlas
        });

        // Snapshot this frame's accumulated atlas + headers into the history for next frame's EMA + reproject test.
        Copy(probeAtlasHistory, probeAtlas);
        // headers → prev (a plain buffer copy; both are committed UAV/SRV-readable structured buffers). probeHeadersPrev
        // rests in NON_PIXEL (it is only ever read as a root SRV from CSIntegrate, a COMPUTE shader). UAV/COPY/NON_PIXEL
        // are ALL compute-legal, so this records identically on either queue — GenericRead (here previously) could NOT,
        // its PIXEL_SHADER_RESOURCE bit is illegal on a COMPUTE list and tripped Close() → E_INVALIDARG.
        Emit(cl =>
        {
            cl.ResourceBarrierTransition(probeHeaders, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
            cl.ResourceBarrierTransition(probeHeadersPrev, ResourceStates.NonPixelShaderResource, ResourceStates.CopyDest);
            cl.CopyResource(probeHeadersPrev, probeHeaders);
            cl.ResourceBarrierTransition(probeHeaders, ResourceStates.CopySource, ResourceStates.UnorderedAccess);
            cl.ResourceBarrierTransition(probeHeadersPrev, ResourceStates.CopyDest, ResourceStates.NonPixelShaderResource);
        });
        spHistoryValid = true;

        // PROBE-SPACE SPATIAL FILTER (the proper blob fix) — blend each probe's atlas cell with the same oct cell
        // of neighbouring probes (depth/normal/world bilateral) → removes the probe-to-probe variance (blob) at its
        // SOURCE, cheaply (probe-res, not full-res). Reads ProbeAtlas (u1), writes ProbeAtlasFiltered (u3); the
        // integrate then reads ProbeAtlasFiltered. BALLISTIC_DX12_AURORA_PROBE_NOFILTER=1 bypasses (copy raw → filtered).
        ToUav(probeAtlas);
        bool probeFilter = Environment.GetEnvironmentVariable("BALLISTIC_DX12_AURORA_PROBE_NOFILTER") != "1";
        if (probeFilter)
        {
            ToUav(probeAtlasFiltered);
            Emit(cl =>
            {
                cl.SetDescriptorHeaps(bindless.Heap);
                cl.SetComputeRootSignature(spRootSig);
                cl.SetPipelineState(spFilterPso);
                SetCommonRoots(cl);
                cl.SetComputeRootDescriptorTable(11, slot11);
                cl.Dispatch((uint)((probesX * octSize + 7) / 8), (uint)((probesY * octSize + 7) / 8), 1);
                cl.ResourceBarrierUnorderedAccessView(probeAtlasFiltered.RenderTarget);   // Filter(w) → SH + Integrate(r)
            });
        }
        else
        {
            Copy(probeAtlasFiltered, probeAtlas);   // copy-barriers already serialise the write
            ToUav(probeAtlas);
            ToUav(probeAtlasFiltered);
        }

        // PROBE-SH PROJECTION — the integrate-cost fix. Project each probe's filtered oct tile into 9 RGB
        // cosine-convolved SH coeffs ONCE (1 thread/probe), so CSIntegrate evaluates an O(1) SH per neighbour
        // probe instead of scanning the oct² tile ×16 per full-res pixel. ProbeAtlasFiltered is the input (UAV),
        // probeSH the output (root UAV). BALLISTIC_DX12_AURORA_PROBE_NOSH=1 falls back to the per-pixel oct integral.
        bool probeSHOn = Environment.GetEnvironmentVariable("BALLISTIC_DX12_AURORA_PROBE_NOSH") != "1";
        if (probeSHOn)
        {
            Emit(cl =>
            {
                cl.SetDescriptorHeaps(bindless.Heap);
                cl.SetComputeRootSignature(spRootSig);
                cl.SetPipelineState(spShPso);
                SetCommonRoots(cl);
                cl.SetComputeRootDescriptorTable(11, slot11);
                cl.Dispatch((uint)((probesX * probesY + 63) / 64), 1, 1);
                cl.ResourceBarrierUnorderedAccessView(probeSH);   // SH(w) → Integrate(r) probeSH
            });
        }

        // INTEGRATE — reads ProbeAtlasFiltered (u3) + the history SRV. The atlas/filtered are UAV. With SH on it
        // reads probeSH (u4) and ignores the oct tile; the spIntegratePso shader picks the path by a CB flag.
        Emit(cl =>
        {
            cl.SetDescriptorHeaps(bindless.Heap);
            cl.SetComputeRootSignature(spRootSig);
            cl.SetPipelineState(spIntegratePso);
            SetCommonRoots(cl);
            cl.SetComputeRootDescriptorTable(11, slot11);
            cl.Dispatch((uint)((indirect.Width + 7) / 8), (uint)((indirect.Height + 7) / 8), 1);
        });
    }

    static float EnvF(string name, float fallback) =>
        float.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.CultureInfo.InvariantCulture,
            out float v) ? v : fallback;

    unsafe void BuildPipelines()
    {
        // --- trace root sig. HeapDirectlyIndexed so the inline-RayQuery hit decode reads ResourceDescriptorHeap[]
        // for per-instance geo/material. The TLAS is a ROOT SRV (t0), NOT a table descriptor: inline RayQuery's
        // Scene reads through a table descriptor unreliably when HeapDirectlyIndexed is set (the table-bound TLAS
        // returned ZERO hits on the RX 9070 XT — proven by the hit/miss probe; RTAO works only because it does NOT
        // set HeapDirectlyIndexed). A root-SRV TLAS sidesteps the heap-indexing mode entirely. CBV b0/b1 +
        // root SRV t0 TLAS + table{t1-t6, u0} + root SRV t7 GpuMaterials / t8 RtInstance[] / t9 Lights + s0/s1. ---
        var cbv0 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var cbv1 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.All);
        var tlasSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All);   // t0 TLAS (root)
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 6, baseShaderRegister: 1);   // t1-t6
        var uavRange = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 0);  // u0
        // #3: probe history texture (t14, slot +7) + motion vectors (t15, slot +8, ghosting reject) in the table
        // tail, after u0 (+6).
        var probeRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 14,
            registerSpace: 0, offsetInDescriptorsFromTableStart: 7);
        var motionRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 15,
            registerSpace: 0, offsetInDescriptorsFromTableStart: 8);
        var table = new RootParameter1(new RootDescriptorTable1(srvRange, uavRange, probeRange, motionRange), ShaderVisibility.All);
        var matSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(7, 0), ShaderVisibility.All);
        var instSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(8, 0), ShaderVisibility.All);
        var lightSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(9, 0), ShaderVisibility.All);
        var cardSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(10, 0), ShaderVisibility.All);  // t10 CardRadiance
        var metaSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(11, 0), ShaderVisibility.All);  // t11 InstanceMeta
        var triClusterSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(12, 0), ShaderVisibility.All);  // t12 TriToCluster (#2A)
        var cardsSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(13, 0), ShaderVisibility.All);  // t13 ClusterCards (Sıra 5)
        var clampSamp = new StaticSamplerDescription(ShaderVisibility.All, 0, 0)
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        var wrapSamp = new StaticSamplerDescription(ShaderVisibility.All, 1, 0)
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        traceRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(
                RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
                new[] { cbv0, cbv1, tlasSrv, table, matSrv, instSrv, lightSrv, cardSrv, metaSrv, triClusterSrv, cardsSrv }, new[] { clampSamp, wrapSamp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("AuroraGi.hlsl");
        byte[] cs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSTrace", "AuroraGi.hlsl");
        tracePso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = traceRootSig, ComputeShader = cs,
        });

        traceCb = new Dx12FrameCb<AuroraConstants>(dev);
        sunCb = new Dx12FrameCb<AuroraSun>(dev);

        // --- combine root sig: CBV b0 (AoStrength) + 5-SRV table (E / albedo / material / depth / GTAO) + clamp
        // sampler. Additive PSO. ---
        var combCbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.Pixel);
        var combRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 5, baseShaderRegister: 0);
        var combTable = new RootParameter1(new RootDescriptorTable1(combRange), ShaderVisibility.Pixel);
        var combSamp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        combineRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { combCbv, combTable }, new[] { combSamp })));

        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSCombine", "AuroraGi.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSCombine", "AuroraGi.hlsl");
        var additive = new BlendDescription(Blend.One, Blend.One);   // src=One dest=One op=Add → ADD onto the HDR color
        GraphicsPipelineStateDescription MakeCombine(byte[] pixel, BlendDescription blend) =>
            new()
            {
                RootSignature = combineRootSig, VertexShader = vs, PixelShader = pixel, InputLayout = null,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
                RasterizerState = RasterizerDescription.CullNone, BlendState = blend,
                DepthStencilState = DepthStencilDescription.None,
                RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat },
                DepthStencilFormat = Format.Unknown, SampleDescription = new SampleDescription(1, 0),
            };
        combinePso = dev.Device.CreateGraphicsPipelineState(MakeCombine(ps, additive));
        byte[] psDebug = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSDebugE", "AuroraGi.hlsl");
        combineDebugPso = dev.Device.CreateGraphicsPipelineState(MakeCombine(psDebug, BlendDescription.Opaque));

        combineSrv = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 10, shaderVisible: true, framesInFlight: dev.FramesInFlight);
        combineCb = new Dx12FrameCb<CombineConstants>(dev);

        BuildCardPipeline();
    }

    // Card-lighting compute (AuroraCardLight.hlsl): TLAS t0 (root SRV) | CardRadiance u0 (root UAV) | sky cube
    // t1 (table, in the bindless tail) | AuroraInstanceMeta t2 / RtInstance[] t3 / GpuMaterials t4 / Lights t5
    // (root SRVs) | b0 | HeapDirectlyIndexed for the bindless geo reads | s0/s1.
    unsafe void BuildCardPipeline()
    {
        var cbv0 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var tlasSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All);   // t0 TLAS
        var uavRoot = new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(0, 0), ShaderVisibility.All);  // u0 CardRadiance
        var skyRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 1);   // t1 sky cube (table)
        var skyTable = new RootParameter1(new RootDescriptorTable1(skyRange), ShaderVisibility.All);
        var instMeta = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(2, 0), ShaderVisibility.All);  // t2
        var rtInst = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(3, 0), ShaderVisibility.All);    // t3
        var mats = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(4, 0), ShaderVisibility.All);      // t4
        var lights = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(5, 0), ShaderVisibility.All);    // t5
        var prevCard = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(6, 0), ShaderVisibility.All);  // t6 PrevCard
        var ageUav = new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(1, 0), ShaderVisibility.All);  // u1 LastUpdated
        var triClus = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(7, 0), ShaderVisibility.All);  // t7 TriToCluster
        var clusTri = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(8, 0), ShaderVisibility.All);  // t8 ClusterToTri
        var clusCards = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(9, 0), ShaderVisibility.All);  // t9 ClusterCards (Sıra 5)
        var emissive = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(10, 0), ShaderVisibility.All);  // t10 EmissiveLights (FAZ 3d NEE)
        var clamp = new StaticSamplerDescription(ShaderVisibility.All, 0, 0)
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1, ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        var wrap = new StaticSamplerDescription(ShaderVisibility.All, 1, 0)
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 1, ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        cardRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(
                RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
                new[] { cbv0, tlasSrv, uavRoot, skyTable, instMeta, rtInst, mats, lights, prevCard, ageUav, triClus, clusTri, clusCards, emissive }, new[] { clamp, wrap })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("AuroraCardLight.hlsl");
        byte[] cs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSMain", "AuroraCardLight.hlsl");
        cardPso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription { RootSignature = cardRootSig, ComputeShader = cs });

        cardCb = new Dx12FrameCb<AuroraCardConstants>(dev);

        BuildDenoisePipeline();
    }

    // Spatial-denoise compute (AuroraGi.hlsl CSDenoise): CBV b0 + table{E t0 / depth t1 / normal t2 SRV, u0 UAV}.
    unsafe void BuildDenoisePipeline()
    {
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srv = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 3, baseShaderRegister: 0, registerSpace: 0, offsetInDescriptorsFromTableStart: 0);
        var uav = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 0, registerSpace: 0, offsetInDescriptorsFromTableStart: 3);
        var table = new RootParameter1(new RootDescriptorTable1(srv, uav), ShaderVisibility.All);
        var dnSamp = new StaticSamplerDescription(ShaderVisibility.All, 0, 0)
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1, ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        denoiseRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, table }, new[] { dnSamp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("AuroraGi.hlsl");
        byte[] cs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSDenoise", "AuroraGi.hlsl");
        denoisePso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription { RootSignature = denoiseRootSig, ComputeShader = cs });

        // PER-PASS CBs (not one shared CB): the denoise runs up to MaxDenoisePasses iterations recorded into ONE
        // pipelined-frame command list, so a single CB would have every pass read the LAST pass's stride (and a
        // single 4-descriptor heap Reset per pass would alias every pass's descriptors → only the last survived,
        // the rest read garbage → the GI went BLACK at passes >= 4). One CB + one 4-descriptor range PER PASS.
        // P0b: each per-pass denoise CB is N-buffered (×FramesInFlight) so overlap can't stomp it; write+bind
        // offset by FrameSlot. FramesInFlight==1 → offset 0 → byte-identical.
        denoiseCbStride = (Marshal.SizeOf<DenoiseConstants>() + 255) & ~255;
        denoiseCbs = new ID3D12Resource[MaxDenoisePasses];
        denoiseCbMappedArr = new System.IntPtr[MaxDenoisePasses];
        for (int i = 0; i < MaxDenoisePasses; i++)
        {
            denoiseCbs[i] = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
                ResourceDescription.Buffer((ulong)(denoiseCbStride * dev.FramesInFlight)), ResourceStates.GenericRead);
            unsafe { denoiseCbMappedArr[i] = (System.IntPtr)denoiseCbs[i].Map<byte>(0); }
        }
        denoiseSrv = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, MaxDenoisePasses * 4,
            shaderVisible: true, framesInFlight: dev.FramesInFlight);

        BuildScreenProbePipeline();
        BuildTemporalPipeline();
    }

    // Common motion-vector temporal resolve pipeline (AuroraTemporal.hlsl): CBV b0 + table{t0 InE, t1 History,
    // t2 Depth, t3 Motion (SRV) + u0 OutE (UAV)} + linear-clamp sampler.
    unsafe void BuildTemporalPipeline()
    {
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srv = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 4, baseShaderRegister: 0, registerSpace: 0, offsetInDescriptorsFromTableStart: 0);
        var uav = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 0, registerSpace: 0, offsetInDescriptorsFromTableStart: 4);
        var table = new RootParameter1(new RootDescriptorTable1(srv, uav), ShaderVisibility.All);
        var samp = new StaticSamplerDescription(ShaderVisibility.All, 0, 0)
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1, ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        tempRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, table }, new[] { samp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("AuroraTemporal.hlsl");
        tempPso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = tempRootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSTemporal", "AuroraTemporal.hlsl"),
        });
        tempCb = new Dx12FrameCb<TemporalConstants>(dev);
        tempSrv = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 8,
            shaderVisible: true, framesInFlight: dev.FramesInFlight);
    }

    // Sıra 1 — screen-probe root sig (mirrors the GI trace) + 3 PSOs (place/trace/integrate) + CBs + a 1-descriptor
    // per-frame heap for the u2 indirect Texture2D UAV (root UAVs are buffers only, so the texture UAV is a table).
    unsafe void BuildScreenProbePipeline()
    {
        var cbv0 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var cbv1 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.All);
        var tlasSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All);   // t0 TLAS
        // DataVolatile (mirrors Dx12ReflectionsPass): the descriptor DATA may change between submits, so D3D must
        // NOT assume DATA_STATIC_WHILE_SET_AT_EXECUTE — that assumption caused the GBV StaticDescriptorInvalid
        // DescriptorChange + InvalidSubresourceState ("enforced because the descriptor is DATA_STATIC") storm when
        // the same bindless tail slots are re-written each frame across 3 separate ExecuteSync submits.
        const DescriptorRangeFlags Vol = DescriptorRangeFlags.DataVolatile;
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 6, baseShaderRegister: 1,
            registerSpace: 0, offsetInDescriptorsFromTableStart: 0, flags: Vol);   // t1-t6
        var atlasUav = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 1,
            registerSpace: 0, offsetInDescriptorsFromTableStart: 6, flags: Vol);   // u1 atlas (after the 6 SRVs)
        var table = new RootParameter1(new RootDescriptorTable1(srvRange, atlasUav), ShaderVisibility.All);
        var matSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(7, 0), ShaderVisibility.All);
        var instSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(8, 0), ShaderVisibility.All);
        var lightSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(9, 0), ShaderVisibility.All);
        var cardSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(10, 0), ShaderVisibility.All);
        var metaSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(11, 0), ShaderVisibility.All);
        var triClusterSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(12, 0), ShaderVisibility.All);
        var headerUav = new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(0, 0), ShaderVisibility.All);   // u0 ProbeHeaders (root UAV, buffer)
        // Per-frame table: u2 indirect (texture UAV) at offset 0, t13 atlas-history (texture SRV) at offset 1.
        var indirectUavRange = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 2,
            registerSpace: 0, offsetInDescriptorsFromTableStart: 0, flags: Vol);   // u2 indirect
        var atlasHistRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 13,
            registerSpace: 0, offsetInDescriptorsFromTableStart: 1, flags: Vol);   // t13 atlas history
        var filteredUavRange = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 3,
            registerSpace: 0, offsetInDescriptorsFromTableStart: 2, flags: Vol);   // u3 ProbeAtlasFiltered (blob-fix filter out / integrate in)
        var indirectTable = new RootParameter1(new RootDescriptorTable1(indirectUavRange, atlasHistRange, filteredUavRange), ShaderVisibility.All);
        var prevHeaderSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(16, 0), ShaderVisibility.All);  // t16 prev headers (root SRV, buffer)
        var spCardsSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(17, 0), ShaderVisibility.All);  // t17 ClusterCards (Sıra 5)
        var probeShUav = new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(4, 0), ShaderVisibility.All);  // u4 ProbeSH (root UAV, buffer — SH irradiance cache)
        var clampSamp = new StaticSamplerDescription(ShaderVisibility.All, 0, 0)
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        var wrapSamp = new StaticSamplerDescription(ShaderVisibility.All, 1, 0)
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        // Roots: 0 cbv0,1 cbv1,2 t0 TLAS,3 table{t1-t6,u1},4-9 t7-t12,10 u0 headers,11 table{u2,t13 hist,u3 filtered},12 t16 prev,13 t17 cards,14 u4 ProbeSH.
        spRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(
                RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
                new[] { cbv0, cbv1, tlasSrv, table, matSrv, instSrv, lightSrv, cardSrv, metaSrv, triClusterSrv, headerUav, indirectTable, prevHeaderSrv, spCardsSrv, probeShUav },
                new[] { clampSamp, wrapSamp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("AuroraScreenProbe.hlsl");
        spPlacePso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = spRootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSPlace", "AuroraScreenProbe.hlsl"),
        });
        spTracePso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = spRootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSProbeTrace", "AuroraScreenProbe.hlsl"),
        });
        spIntegratePso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = spRootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSIntegrate", "AuroraScreenProbe.hlsl"),
        });
        spFilterPso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = spRootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSProbeFilter", "AuroraScreenProbe.hlsl"),
        });
        spShPso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = spRootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSProbeSH", "AuroraScreenProbe.hlsl"),
        });

        spProbeCb = new Dx12FrameCb<ProbeConstants>(dev);
        spSunCb = new Dx12FrameCb<AuroraSun>(dev);
        // (No separate descriptor heap: u2 indirect + t13 atlas history live in the ONE bindless heap at the
        // screen-probe tail — a second shader-visible heap can't be bound simultaneously.)
    }

    const int MaxDenoisePasses = 5;
    ID3D12Resource[] denoiseCbs;
    System.IntPtr[] denoiseCbMappedArr;
    int denoiseCbStride;        // P0b: 256-aligned per-frame slab stride (each denoiseCb is ×FramesInFlight)
    long DenoiseCbOffset => (long)dev.FrameSlot * denoiseCbStride;   // 0 when overlap off

    // P7 #1b — the indirect E (trace) + the denoise scratch run at HALF render resolution (the dominant cost in
    // the baseline was this geometry-independent full-res trace+denoise floor, ~1.2ms; diffuse indirect is
    // low-frequency so half-res is visually free with a depth-aware upsample in the combine). The combine still
    // reads the FULL-res G-buffer (albedo/depth/AO) and depth-aware-upsamples the half-res E. fullW/fullH are
    // kept so the combine knows the upsample ratio. BALLISTIC_DX12_AURORA_RESSCALE overrides (1 = full-res A/B,
    // 2 = half (default), 4 = quarter). Committed (cross-pass scratch; never pooled).
    int fullW, fullH;
    public void Resize(int w, int h)
    {
        fullW = Math.Max(1, w); fullH = Math.Max(1, h);
        // Default FULL-res. Measured on RX 9070 XT: half/quarter-res gave NO perf win (Aurora cost here is RT-
        // traversal/dispatch-bound, not pixel-bound) but DID cost quality (Cornell/Bistro hotspot ~5-8%). So the
        // scale stays opt-in (BALLISTIC_DX12_AURORA_RESSCALE=2/4) for 4K / weak-GPU cases where pixel count bites;
        // the depth-aware upsample + UV-sampled trace/denoise are kept so it's correct when enabled.
        // #3 PROBE: the trace accumulates temporally. Default scale = 1 (FULL-res) — measured: half-res probes left
        // a visible 2x2 'kare' block sparkle at higher GI intensity, and the cost is RT-traversal-bound (pixel
        // count doesn't move the frame time here), so full-res is free AND cleaner. The temporal accumulation is
        // what actually kills the per-ray sparkle. RESSCALE=2/4 stays available for weak-GPU / 4K. The depth-aware
        // combine still upsamples when scale > 1.
        int scale = Math.Clamp((int)EnvF("BALLISTIC_DX12_AURORA_RESSCALE", 1f), 1, 4);
        int lw = Math.Max(1, fullW / scale), lh = Math.Max(1, fullH / scale);
        indirect?.Dispose();
        indirectFiltered?.Dispose();
        indirectFilteredB?.Dispose();
        probeHistory?.Dispose();
        indirect = new Dx12OffscreenTarget(dev, lw, lh, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: true);
        indirectFiltered = new Dx12OffscreenTarget(dev, lw, lh, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: true);
        // Async-GI second filtered buffer (the previous-frame E the decoupled combine reads). Allocated
        // unconditionally (cheap, one HDR target) so a runtime async-door flip needs no reallocation; untouched
        // when the door is off. A resize invalidates the cross-frame history → first async frame skips the combine.
        indirectFilteredB = new Dx12OffscreenTarget(dev, lw, lh, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: true);
        asyncHistoryValid = false;
        probeHistory = new Dx12OffscreenTarget(dev, lw, lh, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: true);
        probeHistoryValid = false;   // a resized history is stale → first frame takes the raw E (alpha=1)
        indirectResolved?.Dispose();   // common motion-vector temporal output (ping target)
        indirectResolved = new Dx12OffscreenTarget(dev, lw, lh, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: true);

        // Sıra 1 — screen-probe grid + octahedral atlas, sized off the `indirect` resolution (the GI front-end
        // resolution). One probe per probeStride×probeStride tile; each probe holds an octSize×octSize oct tile.
        // Default stride 24 (tuned): vs 16 it is CHEAPER (Bistro GI 3.72→3.31ms, ~11%) AND slightly SMOOTHER
        // (more full-res pixels averaged per probe → lower variance: Bistro grain 0.504→0.478, SunTemple 0.262→
        // 0.251), with identical mean/coverage. Larger strides start to blob on dense thin geometry; 24 is the
        // measured sweet spot. BALLISTIC_DX12_AURORA_PROBE_STRIDE overrides.
        probeStride = Math.Clamp((int)EnvF("BALLISTIC_DX12_AURORA_PROBE_STRIDE", 24f), 4, 64);
        // octSize: the env door wins (A/B); otherwise keep the field the quality tier set this frame (default 6,
        // Balanced). Record() reallocates the atlases when the tier changes octSize between frames (EnsureProbeAtlas).
        octSize = Math.Clamp((int)EnvF("BALLISTIC_DX12_AURORA_PROBE_OCT", octSize), 4, 16);
        probesX = (lw + probeStride - 1) / probeStride;
        probesY = (lh + probeStride - 1) / probeStride;
        probeHeaderCount = Math.Max(probesX * probesY, 1);
        probeHeaders?.Dispose();
        // ProbeHeader = 2× float4 = 32 bytes. UAV structured buffer (root UAV).
        probeHeaders = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(probeHeaderCount * 32), ResourceFlags.AllowUnorderedAccess),
            ResourceStates.UnorderedAccess);
        probeHeadersPrev?.Dispose();   // Sıra 3: previous-frame headers (reproject reject) — copy dest / root SRV
        // NON_PIXEL (not GenericRead): this buffer is only ever read as a root SRV from CSIntegrate (a COMPUTE
        // shader), never in a pixel stage. NON_PIXEL is legal in BOTH the graphics and the async-compute path,
        // whereas GenericRead's PIXEL_SHADER_RESOURCE bit cannot be expressed on a COMPUTE command list
        // (Close() → E_INVALIDARG). Created in this resting state so the first CopyDest transition matches.
        probeHeadersPrev = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(probeHeaderCount * 32)), ResourceStates.NonPixelShaderResource);
        // SH irradiance cache: 7 float4 (112 bytes) per probe. Sized off probe COUNT (oct-independent), so it
        // survives a tier octSize change (only the atlas trio re-sizes there). CSProbeSH writes it; CSIntegrate reads.
        probeSH?.Dispose();
        probeShCapacity = probeHeaderCount;
        probeSH = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(probeShCapacity * 7 * 16), ResourceFlags.AllowUnorderedAccess),
            ResourceStates.UnorderedAccess);
        ReallocProbeAtlas();
    }

    // (Re)allocate the octahedral atlas trio — the ONLY resources sized off octSize. Called by Initialize AND by
    // Record when the quality tier changes octSize between frames (so a runtime tier swap re-sizes the atlas
    // without a full GI Initialize). Resets spHistoryValid: a re-sized atlas history is stale (first frame raw).
    void ReallocProbeAtlas()
    {
        probeAtlas?.Dispose();
        probeAtlas = new Dx12OffscreenTarget(dev, Math.Max(probesX * octSize, 1), Math.Max(probesY * octSize, 1),
            withDepth: false, colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: true);
        probeAtlasFiltered?.Dispose();   // probe-space spatial-filtered atlas (blob fix)
        probeAtlasFiltered = new Dx12OffscreenTarget(dev, Math.Max(probesX * octSize, 1), Math.Max(probesY * octSize, 1),
            withDepth: false, colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: true);
        probeAtlasHistory?.Dispose();   // Sıra 3: previous-frame accumulated atlas (EMA source)
        probeAtlasHistory = new Dx12OffscreenTarget(dev, Math.Max(probesX * octSize, 1), Math.Max(probesY * octSize, 1),
            withDepth: false, colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: true);
        spHistoryValid = false;   // a resized atlas history is stale → first frame takes the raw trace (alpha=1)
    }

    // ===== FAZ 4: NRD (ReBLUR) integration =====
    // Lazy-build the NRD denoiser + NRD-mode trace PSO + guide-pack + unpack pipelines + the packed-signal buffers.
    // Returns false (and self-disables) on any failure → Record falls back to Aurora's own temporal accumulator.
    bool EnsureNrd()
    {
        if (nrdReady) return true;
        if (nrdTried) return false;
        nrdTried = true;
        try {
            void Step(string s) { if (Environment.GetEnvironmentVariable("BALLISTIC_DX12_AURORA_NRD_DIAG") == "1") Console.WriteLine($"[NRD enable] {s}"); }
            nrd = new Dx12NrdDenoiser(dev);
            if (!nrd.Initialize(fullW, fullH)) { nrd.Dispose(); nrd = null; return false; }
            Step("denoiser init OK");

            // --- NRD-mode trace root sig = the normal trace layout + u1 (NrdRadianceHitDist, root UAV) + b2 (cbv2).
            var cbv0 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
            var cbv1 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.All);
            var cbv2 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(2, 0), ShaderVisibility.All);   // b2 NRD const
            var tlasSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All);
            var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 6, baseShaderRegister: 1);
            var uavRange = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 0);
            var probeRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 14, registerSpace: 0, offsetInDescriptorsFromTableStart: 7);
            var motionRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 15, registerSpace: 0, offsetInDescriptorsFromTableStart: 8);
            var table = new RootParameter1(new RootDescriptorTable1(srvRange, uavRange, probeRange, motionRange), ShaderVisibility.All);
            var matSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(7, 0), ShaderVisibility.All);
            var instSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(8, 0), ShaderVisibility.All);
            var lightSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(9, 0), ShaderVisibility.All);
            var cardSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(10, 0), ShaderVisibility.All);
            var metaSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(11, 0), ShaderVisibility.All);
            var triClusterSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(12, 0), ShaderVisibility.All);
            var cardsSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(13, 0), ShaderVisibility.All);
            // u1 NrdRadianceHitDist: a Texture2D UAV CANNOT be a root descriptor (only buffers can) → use a 1-entry
            // descriptor table (range at u1, its own offset so it doesn't alias the bindless table).
            var nrdUavRange = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 1);
            var nrdUav = new RootParameter1(new RootDescriptorTable1(nrdUavRange), ShaderVisibility.All);
            var cs = new StaticSamplerDescription(ShaderVisibility.All, 0, 0) { Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1, ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue };
            var ws = new StaticSamplerDescription(ShaderVisibility.All, 1, 0) { Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 1, ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue };
            Step("before trace rootsig"); nrdTraceRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
                new RootSignatureDescription1(RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
                    new[] { cbv0, cbv1, cbv2, tlasSrv, table, matSrv, instSrv, lightSrv, cardSrv, metaSrv, triClusterSrv, cardsSrv, nrdUav }, new[] { cs, ws })));

            byte[] traceCs = Dx12NrdDenoiser.CompileWithNrd(DxcShaderStage.Compute, "AuroraGi.hlsl", "CSTrace", "#define AURORA_NRD_MODE 1\n");
            Step("trace rootsig OK"); nrdTracePso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription { RootSignature = nrdTraceRootSig, ComputeShader = traceCs });
            nrdConstCb = new Dx12FrameCb<AuroraNrdConstants>(dev);

            // --- guide-pack pipeline (CBV b0 + table{Depth t0, Normal t1, PrevViewZ t2 SRV; OutMv u0, OutNR u1, OutViewZ u2}).
            var pCbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
            var pSrv = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 3, baseShaderRegister: 0);
            var pUav = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 3, baseShaderRegister: 0, registerSpace: 0, offsetInDescriptorsFromTableStart: 3);
            var pTable = new RootParameter1(new RootDescriptorTable1(pSrv, pUav), ShaderVisibility.All);
            Step("trace pso OK"); nrdPackRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(new RootSignatureDescription1(RootSignatureFlags.None, new[] { pCbv, pTable }, new[] { cs })));
            byte[] packCs = Dx12NrdDenoiser.CompileWithNrd(DxcShaderStage.Compute, "Nrd/AuroraNrdPack.hlsl", "CSMain");
            Step("pack rootsig OK"); nrdPackPso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription { RootSignature = nrdPackRootSig, ComputeShader = packCs });
            nrdPackCb = new Dx12FrameCb<NrdPackConstants>(dev);

            // --- unpack pipeline (table{NrdOut t0, Depth t1 SRV; OutE u0}).
            var uSrv = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 2, baseShaderRegister: 0);
            var uUav = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 0, registerSpace: 0, offsetInDescriptorsFromTableStart: 2);
            var uTable = new RootParameter1(new RootDescriptorTable1(uSrv, uUav), ShaderVisibility.All);
            Step("pack pso OK"); nrdUnpackRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(new RootSignatureDescription1(RootSignatureFlags.None, new[] { uTable }, new[] { cs })));
            byte[] unpackCs = Dx12NrdDenoiser.CompileWithNrd(DxcShaderStage.Compute, "Nrd/AuroraNrdUnpack.hlsl", "CSMain");
            nrdUnpackPso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription { RootSignature = nrdUnpackRootSig, ComputeShader = unpackCs });

            // --- packed-signal buffers (NRD ABI formats). All UAV+SRV, full-res.
            Step("unpack OK; making buffers"); nrdRadianceHitDist = MakeTex(Format.R16G16B16A16_Float);
            nrdViewZ = MakeTex(Format.R16_Float);
            nrdViewZPrev = MakeTex(Format.R16_Float);
            nrdViewZPrevValid = false;
            nrdNormalRough = MakeTex(Format.R10G10B10A2_UNorm);
            nrdMotion = MakeTex(Format.R16G16B16A16_Float);
            nrdOut = MakeTex(Format.R16G16B16A16_Float);

            nrdScratch = new Dx12DescriptorHeap(dev, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                16, shaderVisible: true, framesInFlight: dev.FramesInFlight);

            nrdReady = true;
            Console.WriteLine("[Aurora] NRD (ReBLUR) temporal denoiser ENGAGED — replacing probe-EMA + à-trous.");
            return true;
        } catch (Exception e) {
            Console.WriteLine($"[Aurora] NRD enable failed → Aurora temporal fallback: {e.Message}");
            DisposeNrd(); nrd?.Dispose(); nrd = null;
            return false;
        }
    }

    // NRD-mode trace+denoise flow. Replaces the probe-EMA + à-trous chain when the NRD door is on: the trace writes
    // raw E + REBLUR-packed radiance/hitDist, a guide-pack pass derives viewZ/normal/motion, NRD denoises, and an
    // unpack writes the clean E into indirectFiltered (combine reads it). Recorded inline on the graphics list.
    unsafe void RecordNrd(Dx12FrameContext ctx, Dx12SceneAS sceneAS, Dx12RtGeometry rtGeo,
                          Dx12ClusteredLights clusteredLights, Dx12GBuffer gbuffer)
    {
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Dx12DescriptorHeap bindless = Dx12Backend.BindlessHeap;
        nrdScratch.Reset();

        // NRD constants: view matrix + REBLUR hit-dist params {A=3, B=0.1, C=20}.
        nrdConstCb.Write(new AuroraNrdConstants { NrdViewMatrix = Matrix4x4.Transpose(ctx.View), NrdHitDistParams = new Vector3(3f, 0.1f, 20f) });

        // 1) NRD-mode trace → indirect (raw E) + nrdRadianceHitDist (packed). Same bindings as the normal trace +
        // cbv2 (NRD const) + u1 (radiance buffer, via a 1-entry descriptor table on the BINDLESS heap tail). The
        // bindless heap is already bound (HeapDirectlyIndexed); we stamp the u1 UAV into a reserved slot and bind it.
        var nrdRadUav = nrdRadianceHitDist;
        int radSlot = Dx12BindlessTail.TailStart - 1;   // one slot below the GI tail, reserved for this UAV
        dev.Device.CreateUnorderedAccessView(nrdRadUav, null,
            new UnorderedAccessViewDescription { Format = Format.R16G16B16A16_Float, ViewDimension = UnorderedAccessViewDimension.Texture2D },
            bindless.Cpu(radSlot));
        dev.ExecuteSync(cl => {
            cl.ResourceBarrierTransition(nrdRadUav, ResourceStates.Common, ResourceStates.UnorderedAccess);
            cl.SetDescriptorHeaps(bindless.Heap);
            cl.SetComputeRootSignature(nrdTraceRootSig);
            cl.SetPipelineState(nrdTracePso);
            cl.SetComputeRootConstantBufferView(0, traceCb.Gpu);
            cl.SetComputeRootConstantBufferView(1, sunCb.Gpu);
            cl.SetComputeRootConstantBufferView(2, nrdConstCb.Gpu);
            cl.SetComputeRootShaderResourceView(3, sceneAS.TlasAddress);
            cl.SetComputeRootDescriptorTable(4, bindless.Gpu(AuroraTableBase));
            cl.SetComputeRootShaderResourceView(5, ctx.GpuDriven.MaterialsGpuAddress);
            cl.SetComputeRootShaderResourceView(6, rtGeo.InstancesGpuAddress);
            cl.SetComputeRootShaderResourceView(7, clusteredLights.LightBufGpuAddress);
            cl.SetComputeRootShaderResourceView(8, scene.CardRadianceWriteGpu);
            cl.SetComputeRootShaderResourceView(9, scene.InstanceMetaGpuAddress);
            cl.SetComputeRootShaderResourceView(10, scene.TriToClusterGpuAddress);
            cl.SetComputeRootShaderResourceView(11, scene.ClusterCardsGpuAddress);
            cl.SetComputeRootDescriptorTable(12, bindless.Gpu(radSlot));   // u1 NrdRadianceHitDist (table)
            cl.Dispatch((uint)((indirect.Width + 7) / 8), (uint)((indirect.Height + 7) / 8), 1);
        });

        // 2) Guide pack: Depth + Normal → viewZ / normal-roughness / 2.5D motion.
        Matrix4x4.Invert(ctx.ViewProj, out Matrix4x4 invVP);
        nrdPackCb.Write(new NrdPackConstants {
            InvViewProj = Matrix4x4.Transpose(invVP), PrevViewProj = ctx.PrevViewProjUnjittered,
            ViewMatrix = Matrix4x4.Transpose(ctx.View), InvResolution = new Vector2(1f / fullW, 1f / fullH),
            PrevViewZValid = nrdViewZPrevValid ? 1f : 0f,
        });
        // SRV view for last frame's viewZ (nrdViewZPrev) at t2 — reprojected fetch gives the 2.5D motion .z.
        var viewZSrvDesc = new ShaderResourceViewDescription {
            Format = Format.R16_Float, ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default, Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
        };
        int pb = nrdScratch.AllocateRange(6);
        dev.Device.CopyDescriptorsSimple(1, nrdScratch.Cpu(pb + 0), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, nrdScratch.Cpu(pb + 1), gbuffer.ColorSrvCpu(1), heapType);   // normal
        dev.Device.CreateShaderResourceView(nrdViewZPrev, viewZSrvDesc, nrdScratch.Cpu(pb + 2));         // t2 prev viewZ
        UavInto(nrdScratch.Cpu(pb + 3), nrdMotion, Format.R16G16B16A16_Float);
        UavInto(nrdScratch.Cpu(pb + 4), nrdNormalRough, Format.R10G10B10A2_UNorm);
        UavInto(nrdScratch.Cpu(pb + 5), nrdViewZ, Format.R16_Float);
        dev.ExecuteSync(cl => {
            foreach (var t in new[] { nrdMotion, nrdNormalRough, nrdViewZ })
                cl.ResourceBarrierTransition(t, ResourceStates.Common, ResourceStates.UnorderedAccess);
            cl.ResourceBarrierTransition(nrdViewZPrev, ResourceStates.Common, ResourceStates.NonPixelShaderResource);
            gbuffer.DepthToNonPixelShaderResource();
            cl.SetDescriptorHeaps(nrdScratch.Heap);
            cl.SetComputeRootSignature(nrdPackRootSig);
            cl.SetPipelineState(nrdPackPso);
            cl.SetComputeRootConstantBufferView(0, nrdPackCb.Gpu);
            cl.SetComputeRootDescriptorTable(1, nrdScratch.Gpu(pb));
            cl.Dispatch((uint)((fullW + 7) / 8), (uint)((fullH + 7) / 8), 1);
            // All NRD inputs to NonPixel SRV for the denoiser; radiance buffer too.
            foreach (var t in new[] { nrdMotion, nrdNormalRough, nrdViewZ, nrdRadUav })
                cl.ResourceBarrierTransition(t, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            // prev-viewZ is consumed (pack already read it) — back to Common for the end-of-frame ping-pong swap.
            cl.ResourceBarrierTransition(nrdViewZPrev, ResourceStates.NonPixelShaderResource, ResourceStates.Common);
            cl.ResourceBarrierTransition(nrdOut, ResourceStates.Common, ResourceStates.UnorderedAccess);
        });

        // 3) NRD denoise.
        var snap = new Dx12NrdDenoiser.Resource[(int)NrdApi.ResourceType.MAX_NUM];
        snap[(int)NrdApi.ResourceType.IN_MV] = new(nrdMotion, Format.R16G16B16A16_Float, ResourceStates.NonPixelShaderResource);
        snap[(int)NrdApi.ResourceType.IN_NORMAL_ROUGHNESS] = new(nrdNormalRough, Format.R10G10B10A2_UNorm, ResourceStates.NonPixelShaderResource);
        snap[(int)NrdApi.ResourceType.IN_VIEWZ] = new(nrdViewZ, Format.R16_Float, ResourceStates.NonPixelShaderResource);
        snap[(int)NrdApi.ResourceType.IN_DIFF_RADIANCE_HITDIST] = new(nrdRadUav, Format.R16G16B16A16_Float, ResourceStates.NonPixelShaderResource);
        snap[(int)NrdApi.ResourceType.OUT_DIFF_RADIANCE_HITDIST] = new(nrdOut, Format.R16G16B16A16_Float, ResourceStates.UnorderedAccess);

        var common = NrdSettings.NrdCommonSettings.Default();
        FillNrdCommon(ref common, ctx);
        var reblur = NrdSettings.ReblurSettings.Default();
        nrd.ResetFrameRings();
        dev.ExecuteSync(cl => nrd.Denoise(cl, common, reblur, snap));
        nrd.SyncPoolStates();

        // 4) Unpack NRD output → indirectFiltered (E). Then bring buffers back to Common for next frame's barriers.
        int ub = nrdScratch.AllocateRange(3);
        dev.Device.CopyDescriptorsSimple(1, nrdScratch.Cpu(ub + 0), gbuffer.DepthSrvCpu, heapType);
        // nrdOut SRV
        dev.Device.CreateShaderResourceView(nrdOut, new ShaderResourceViewDescription {
            Format = Format.R16G16B16A16_Float, ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default, Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
        }, nrdScratch.Cpu(ub + 1));
        UavInto(nrdScratch.Cpu(ub + 2), indirectFiltered.RenderTarget, Dx12OffscreenTarget.HdrFormat);
        ToUav(indirectFiltered);
        dev.ExecuteSync(cl => {
            cl.ResourceBarrierTransition(nrdOut, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            cl.SetDescriptorHeaps(nrdScratch.Heap);
            cl.SetComputeRootSignature(nrdUnpackRootSig);
            cl.SetPipelineState(nrdUnpackPso);
            cl.SetComputeRootDescriptorTable(0, nrdScratch.Gpu(ub));
            cl.Dispatch((uint)((fullW + 7) / 8), (uint)((fullH + 7) / 8), 1);
            // Reset NRD I/O buffers to Common for next frame's Common→UAV barriers.
            foreach (var t in new[] { nrdMotion, nrdNormalRough, nrdViewZ, nrdRadUav })
                cl.ResourceBarrierTransition(t, ResourceStates.NonPixelShaderResource, ResourceStates.Common);
            cl.ResourceBarrierTransition(nrdOut, ResourceStates.NonPixelShaderResource, ResourceStates.Common);
        });
        ToShaderRead(indirectFiltered);

        // Ping-pong: this frame's viewZ becomes next frame's prev-viewZ (both rest in Common). After the first
        // successful pack the prev buffer holds a real frame → enable the 2.5D .z reprojection next frame.
        (nrdViewZ, nrdViewZPrev) = (nrdViewZPrev, nrdViewZ);
        nrdViewZPrevValid = true;
    }

    void UavInto(CpuDescriptorHandle h, ID3D12Resource res, Format f) {
        dev.Device.CreateUnorderedAccessView(res, null,
            new UnorderedAccessViewDescription { Format = f, ViewDimension = UnorderedAccessViewDimension.Texture2D }, h);
    }

    // Fill NRD common settings from the frame context. NRD wants column-major (vector-is-column), NON-jittered
    // matrices; System.Numerics is row-major row-vector, so the column-major store is the transpose: dst[col*4+row].
    // ColMajor() does that store for one matrix into a fixed float[16].
    static unsafe void ColMajor(float* dst, in Matrix4x4 m) {
        dst[0]=m.M11; dst[1]=m.M21; dst[2]=m.M31;  dst[3]=m.M41;
        dst[4]=m.M12; dst[5]=m.M22; dst[6]=m.M32;  dst[7]=m.M42;
        dst[8]=m.M13; dst[9]=m.M23; dst[10]=m.M33; dst[11]=m.M43;
        dst[12]=m.M14;dst[13]=m.M24;dst[14]=m.M34; dst[15]=m.M44;
    }
    unsafe void FillNrdCommon(ref NrdSettings.NrdCommonSettings c, Dx12FrameContext ctx) {
        Matrix4x4 proj = ctx.ProjUnjittered, view = ctx.View;
        // Real previous-frame view: proj is static frame-to-frame (FOV/aspect constant), so
        // prevView = PrevViewProjUnjittered · inv(proj). This gives NRD true camera motion (ghosting reject)
        // without threading a separate prev-view field through the frame context. On the very first frame
        // PrevViewProjUnjittered == current (renderer seeds it), so prevView == view → no spurious motion.
        Matrix4x4 prevView = view;
        if (Matrix4x4.Invert(proj, out Matrix4x4 invProj))
            prevView = ctx.PrevViewProjUnjittered * invProj;
        fixed (float* p = c.ViewToClipMatrix)     ColMajor(p, proj);
        fixed (float* p = c.ViewToClipMatrixPrev) ColMajor(p, proj);   // proj static frame-to-frame
        fixed (float* p = c.WorldToViewMatrix)     ColMajor(p, view);
        fixed (float* p = c.WorldToViewMatrixPrev) ColMajor(p, prevView);
        c.ResourceSize[0] = (ushort)fullW; c.ResourceSize[1] = (ushort)fullH;
        c.ResourceSizePrev[0] = (ushort)fullW; c.ResourceSizePrev[1] = (ushort)fullH;
        c.RectSize[0] = (ushort)fullW; c.RectSize[1] = (ushort)fullH;
        c.RectSizePrev[0] = (ushort)fullW; c.RectSizePrev[1] = (ushort)fullH;
        c.FrameIndex = (uint)frameCounter;
        c.AccumulationMode = probeHistoryValid ? NrdApi.AccumulationMode.CONTINUE : NrdApi.AccumulationMode.CLEAR_AND_RESTART;
        c.IsMotionVectorInWorldSpace = false;
    }

    ID3D12Resource MakeTex(Format f) {
        var rd = ResourceDescription.Texture2D(f, (uint)fullW, (uint)fullH, 1, 1);
        rd.Flags = ResourceFlags.AllowUnorderedAccess;
        return dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None, rd, ResourceStates.UnorderedAccess);
    }

    void DisposeNrd() {
        nrdTracePso?.Dispose(); nrdTraceRootSig?.Dispose(); nrdConstCb?.Dispose();
        nrdPackPso?.Dispose(); nrdPackRootSig?.Dispose(); nrdPackCb?.Dispose();
        nrdUnpackPso?.Dispose(); nrdUnpackRootSig?.Dispose();
        nrdRadianceHitDist?.Dispose(); nrdViewZ?.Dispose(); nrdViewZPrev?.Dispose(); nrdNormalRough?.Dispose(); nrdMotion?.Dispose(); nrdOut?.Dispose();
        nrdScratch?.Dispose();
        nrdTracePso = null; nrdPackPso = null; nrdUnpackPso = null; nrdScratch = null;
        nrdRadianceHitDist = nrdViewZ = nrdViewZPrev = nrdNormalRough = nrdMotion = nrdOut = null;
        nrdViewZPrevValid = false;
        nrdReady = false;
    }

    public void Dispose()
    {
        nrd?.Dispose();
        scene.Dispose();
        emissiveLights.Dispose();
        tracePso?.Dispose(); traceRootSig?.Dispose(); traceCb?.Dispose(); sunCb?.Dispose();
        cardPso?.Dispose(); cardRootSig?.Dispose(); cardCb?.Dispose();
        denoisePso?.Dispose(); denoiseRootSig?.Dispose(); denoiseSrv?.Dispose();
        if (denoiseCbs != null) foreach (var cb in denoiseCbs) cb?.Dispose();
        combinePso?.Dispose(); combineDebugPso?.Dispose(); combineRootSig?.Dispose(); combineSrv?.Dispose(); combineCb?.Dispose();
        indirect?.Dispose(); indirectFiltered?.Dispose(); indirectFilteredB?.Dispose(); probeHistory?.Dispose();
        tempPso?.Dispose(); tempRootSig?.Dispose(); tempCb?.Dispose(); tempSrv?.Dispose(); indirectResolved?.Dispose();
        spPlacePso?.Dispose(); spTracePso?.Dispose(); spIntegratePso?.Dispose(); spFilterPso?.Dispose(); spRootSig?.Dispose();
        probeAtlasFiltered?.Dispose();
        spProbeCb?.Dispose(); spSunCb?.Dispose();
        probeHeaders?.Dispose(); probeHeadersPrev?.Dispose(); probeAtlas?.Dispose(); probeAtlasHistory?.Dispose();
        probeSH?.Dispose(); spShPso?.Dispose();
    }
}
