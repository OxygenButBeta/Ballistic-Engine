using System;
using System.Numerics;
using System.Runtime.InteropServices;
using BallisticEngine;          // RuntimeSet, IStaticMeshRenderer
using Vortice.Direct3D;         // PrimitiveTopology
using Vortice.Direct3D12;
using Vortice.Dxc;              // DxcShaderStage
using Vortice.DXGI;             // Format, SampleDescription

namespace BallisticEngine.DX12;

// Lumen V2 — the single product-facing GI pass (plan §Target Shape: one `Lumen` path; screen traces first,
// hardware RT for off-screen hits, surface/radiance cache for stable indirect). Event = GlobalIllumination
// (500), the slot the legacy GI pass occupied (after Transparents, before Fog).
//
// P2 (THIS milestone — "minimal truthful GI"): one diffuse bounce, NO surface cache, NO temporal history.
//   1. CSTrace (LumenGi.hlsl) integrates incoming diffuse irradiance per pixel: screen-trace the depth buffer
//      first (free near-field contact bounce), inline-RayQuery the scene TLAS on a screen miss (off-screen +
//      occluded), sky/IBL on an RT miss. RT hits are shaded with REAL first-bounce radiance (emissive + sun
//      + punctual, shadow-rayed, × bindless albedo). Writes incoming irradiance E into `indirect`.
//   2. PSCombine adds E*albedo*ao/PI into the HDR scene color (additive One/One). The deferred pass already
//      suppressed its IBL diffuse ambient (ctx.LumenActiveThisFrame → UseIBLDiffuse=0), so no double count.
// "Noisy but truthful": low ray count, no denoise. Gates are correctness — black room black, color bleed
// bleeds, thin wall no leak. Cards (P3) + radiance cache/temporal (P4) build on this.
//
// Owns the Lumen scene substrate (Dx12LumenScene) and `indirect`. Gated behind BALLISTIC_DX12_LUMEN; default-
// off = no substrate alloc + no-op Record. HW-RT only (plan gate #6: no hidden SSGI fallback).
public sealed class Dx12LumenGiPass : IRenderPass, IDisposable
{
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.GlobalIllumination;
    public string Name => "Lumen GI";

    readonly Dx12Device dev;
    readonly Dx12LumenScene scene;

    // The card radiance cache (+ per-instance meta) the Reflections pass (event 600, after this) samples so
    // rough reflections read the SAME multi-bounce GI the diffuse sees (plan P5). Exposed read-only; valid only
    // after a successful Ensure this frame (the reflections pass also gates on ctx.LumenActiveThisFrame).
    public Dx12LumenScene Scene => scene;

    public Dx12LumenGiPass(Dx12Device device, int width, int height)
    {
        dev = device;
        scene = new Dx12LumenScene(device);
        BuildPipelines();
        Resize(width, height);
    }

    // The product door. Lumen is driven by the GlobalIllumination VOLUME (ctx.PostFX.LumenEnabled, default ON —
    // plan §Target Shape: one product-facing mode). The BALLISTIC_DX12_LUMEN env door overrides for A/B:
    // "1" forces on, "0" forces off, unset → follow the volume. Always hard-gated by hardware ray tracing in
    // WouldRun (no HW RT → Lumen unavailable, plan gate #6: NO hidden screen-space fallback).
    static int envDoor = -2;   // -2 unread, -1 unset(follow volume), 0 force-off, 1 force-on
    static bool Armed(Dx12FrameContext ctx) {
        if (envDoor == -2) {
            string v = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN");
            envDoor = v == "1" ? 1 : v == "0" ? 0 : -1;
        }
        return envDoor == 1 || (envDoor == -1 && ctx.PostFX.LumenEnabled);
    }

    // The frame-level "Lumen runs" predicate, shared with the orchestrator (which mirrors it into
    // ctx.LumenActiveThisFrame so the deferred pass suppresses its IBL diffuse ambient before this pass adds
    // its own diffuse indirect). Lumen is HW-RT only — no hidden SSGI fallback (plan gate #6).
    public static bool WouldRun(Dx12FrameContext ctx) =>
        !ctx.Doors.Minimal && Armed(ctx) && ctx.Dev.HasHardwareRayTracing && ctx.Dxr?.SceneAS != null;

    public bool Enabled(Dx12FrameContext ctx) => WouldRun(ctx);

    // ---- trace (inline RayQuery compute) ----
    ID3D12RootSignature traceRootSig;   // HeapDirectlyIndexed; CBV b0/b1 + table{t0-t6, u0} + root SRV t7/t8/t9 + s0/s1
    ID3D12PipelineState tracePso;
    ID3D12Resource traceCb, sunCb;
    unsafe byte* traceCbMapped, sunCbMapped;
    const int LumenTableBase = Dx12BindlessTail.LumenTableBase;
    Dx12OffscreenTarget indirect;       // probe-res RGBA16F incoming irradiance E (cross-pass scratch; rebuilt on resize)

    // ---- #3 PROBE TEMPORAL ACCUMULATION ----
    // The trace is a low-res PROBE gather (1 trace point per probe-grid cell). A few rays/probe/frame is noisy, so
    // the probe radiance is ACCUMULATED across frames (cache-space-like temporal EMA) → many effective rays at low
    // cost, the low-variance final gather Lumen's screen probes give. History is probe-res, depth-guarded against a
    // disocclusion (camera move / geometry change flushes a probe instead of smearing). `probeHistory` holds last
    // frame's accumulated E + its depth in .a; cross-frame so it is pass-owned (NEVER pooled). RGBA16F: rgb=E, a=depth.
    Dx12OffscreenTarget probeHistory;
    bool probeHistoryValid;

    // ---- spatial denoise (edge-aware blur of the per-pixel indirect E) ----
    ID3D12RootSignature denoiseRootSig; // CBV b0 + table{t0-t2 SRV, u0 UAV}
    ID3D12PipelineState denoisePso;
    // (per-pass denoise CBs live in denoiseCbs[]/denoiseCbMappedArr[] — see BuildDenoisePipeline)
    Dx12DescriptorHeap denoiseSrv;      // 4 descriptors (E/depth/normal SRV + filtered UAV)
    Dx12OffscreenTarget indirectFiltered; // full-res filtered E the combine reads

    [StructLayout(LayoutKind.Sequential)]
    struct DenoiseConstants { public Vector2 Texel; public float Step; public float Enabled; }

    // ---- combine (additive fullscreen) ----
    ID3D12RootSignature combineRootSig; // 4-SRV table + sampler
    ID3D12PipelineState combinePso;
    ID3D12PipelineState combineDebugPso; // OPAQUE replace — BALLISTIC_DX12_LUMEN_DEBUG=1 shows raw E (no add)
    ID3D12Resource combineCb;
    unsafe byte* combineCbMapped;
    Dx12DescriptorHeap combineSrv;      // 5 SRVs per pass (E/albedo/material/depth/GTAO)

    [StructLayout(LayoutKind.Sequential)]
    struct CombineConstants { public float AoStrength; public Vector2 IndirectTexel; public float Pad0; }   // IndirectTexel = 1/half-res for the depth-aware upsample

    [StructLayout(LayoutKind.Sequential)]
    struct LumenConstants
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
    struct LumenSun { public Vector3 SunDir; public float SunBias; public Vector3 SunColor; public float LightCount; }

    // Card-lighting pass (LumenCardLight.hlsl): lights every triangle "card" before the trace samples them.
    ID3D12RootSignature cardRootSig;
    ID3D12PipelineState cardPso;
    ID3D12Resource cardCb;
    unsafe byte* cardCbMapped;
    const int CardSkyTableBase = Dx12BindlessTail.LumenCardTableBase;

    [StructLayout(LayoutKind.Sequential)]
    struct LumenCardConstants
    {
        public Vector3 SunDir; public float SunBias;
        public Vector3 SunColor; public float LightCount;
        public uint InstanceCount; public uint TotalTris; public float SkyIntensity; public float UseSky;
        public float SkyVisRays; public float EmaAlpha; public float BounceRays; public float HistoryValid;
        public uint FrameIndex; public uint UpdateStride; public uint ForceFull; public uint TexelDim;   // P7 #1; Sıra 5 mesh-card grid edge
    }

    // ---- Sıra 1: SCREEN-SPACE RADIANCE PROBES (LumenScreenProbe.hlsl) ----
    // A sparse grid of radiance probes (one per ProbeStride×ProbeStride screen tile) replaces the per-pixel trace
    // as the GI front end: far fewer trace points + more rays each → lower variance AND lower cost (the published
    // Lumen final-gather). Three compute passes (place → trace → integrate) write the SAME full-res `indirect`
    // irradiance E buffer the per-pixel CSTrace did, so the downstream probe-temporal + denoise + combine chain is
    // untouched. Gated behind BALLISTIC_DX12_LUMEN_SCREENPROBE ("1" force on, "0" force off, unset → default).
    ID3D12RootSignature spRootSig;       // HeapDirectlyIndexed; CBV b0/b1 + root SRV t0 TLAS + table{t1-t6, u1 atlas} + root SRV t7-t12 + root UAV u0 headers / u2 indirect + s0/s1
    ID3D12PipelineState spPlacePso, spTracePso, spIntegratePso;
    ID3D12Resource spProbeCb, spSunCb;
    unsafe byte* spProbeCbMapped, spSunCbMapped;
    ID3D12Resource probeHeaders;         // StructuredBuffer<ProbeHeader> (root UAV u0) — sized ProbesX*ProbesY
    ID3D12Resource probeHeadersPrev;     // Sıra 3: previous frame's headers (reproject reject) — root SRV t16
    Dx12OffscreenTarget probeAtlas;      // octahedral radiance atlas, (ProbesX*OctSize) × (ProbesY*OctSize), RGBA16F UAV
    Dx12OffscreenTarget probeAtlasHistory; // Sıra 3: previous frame's accumulated atlas (EMA source) — table t13
    bool spHistoryValid;
    int probeStride = 16, octSize = 8;
    int probesX, probesY, probeHeaderCount;
    const int SpTableBase = Dx12BindlessTail.LumenScreenProbeTableBase;

    [StructLayout(LayoutKind.Sequential)]
    struct ProbeConstants
    {
        public Matrix4x4 InvViewProj;
        public Matrix4x4 ViewProj;
        public Vector3 CameraPos; public float Intensity;
        public Vector2 FullTexel; public float RayCount; public float FrameIndex;
        public float NormalBias; public float MaxRayDist; public float UseCards; public float ScreenSteps;
        public float SkyIntensity; public float UseSky; public float UseScreenTrace; public float ScreenRange;
        public float FalloffDist; public float ProbeTile; public float ProbeStride; public float OctSize;
        public uint ProbesX; public uint ProbesY; public uint FullW; public uint FullH;
        public float HistoryValid; public float ProbeEma; public float TexelDim; public float SpPad1;
    }

    static int spEnvDoor = -2;   // -2 unread, -1 unset(default), 0 force-off, 1 force-on
    bool WantScreenProbe(Dx12FrameContext ctx)
    {
        if (spEnvDoor == -2)
        {
            string v = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_SCREENPROBE");
            spEnvDoor = v == "1" ? 1 : v == "0" ? 0 : -1;
        }
        if (spEnvDoor == 1) return true;    // explicit force-on (overrides the deterministic guard, for A/B)
        if (spEnvDoor == 0) return false;
        // DEFAULT path. Screen probes are the GI front end in PLAY — they accumulate over real frames into a clean,
        // low-variance gather (measured: on par with / smoother than the per-pixel trace; SunTemple grain 0.262 vs
        // 0.304, Bistro 0.504 vs 0.562). BUT a DETERMINISTIC CAPTURE forces the card cache's temporal EMA + multi-
        // bounce OFF (for byte-reproducibility), which starves the sparse screen-probe gather (the per-pixel trace,
        // shooting a ray from EVERY pixel, tolerates the weaker cache far better). So under a deterministic capture
        // we fall back to the per-pixel trace → golden/diffable captures stay stable, play uses the better gather.
        return ScreenProbeDefaultOn && !ctx.DeterministicCapture;
    }
    // Screen probes are the default GI gather in play (proven on par / smoother). The deterministic-capture guard in
    // WantScreenProbe keeps golden captures on the per-pixel path.
    const bool ScreenProbeDefaultOn = true;

    int frameCounter;

    // P7 #1 update-budget dirty tracking: the sun dir/color + light count the cache was last FULLY relit with.
    // A change (or a topology rebuild) → ForceFull this frame so the round-robin budget never starves a light
    // change of latency. NaN sentinel forces a full relight on the first frame.
    Vector3 prevSunDir = new(float.NaN, 0, 0);
    Vector3 prevSunColor;
    float prevLightCount = -1f;

    public unsafe void Record(Dx12FrameContext ctx)
    {
        // Build/refresh the substrate (shared TLAS + bindless geo + card table + atlases) and log its counts.
        if (!scene.Ensure(ctx))
            return;   // no valid scene AS → nothing to trace (Lumen is HW-RT only; no SSGI fallback)

        var sceneAS = ctx.Dxr.SceneAS;
        var rtGeo = ctx.Dxr.RtGeometry;
        if (!rtGeo.Valid) return;

        var gbuffer = ctx.GBuffer;
        var ibl = ctx.Ibl;
        var clusteredLights = ctx.ClusteredLights;
        var target = ctx.SceneColor;

        Matrix4x4.Invert(ctx.ViewProj, out Matrix4x4 invVP);

        // Dials: the GlobalIllumination VOLUME (ctx.PostFX) drives them; the BALLISTIC_DX12_LUMEN_* env doors
        // override for A/B (EnvF returns the env value when set, else the volume-supplied fallback).
        var fx = ctx.PostFX;
        float intensity = EnvF("BALLISTIC_DX12_LUMEN_INTENSITY", fx.LumenIntensity);
        float rayCount = MathF.Round(EnvF("BALLISTIC_DX12_LUMEN_RAYS", fx.LumenRayCount));
        float maxDist = EnvF("BALLISTIC_DX12_LUMEN_DIST", 40f);
        float skyIntensity = EnvF("BALLISTIC_DX12_LUMEN_SKY", fx.LumenSkyIntensity);
        bool useSky = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_NOSKY") != "1";
        bool useCards = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_NOCARDS") != "1";

        Vector3 sunDirN = ctx.LightDir.LengthSquared() < 1e-8f ? Vector3.UnitY : Vector3.Normalize(ctx.LightDir);

        // === CARD LIGHTING (P3): light every triangle card into CardRadiance before the trace samples them.
        // 1D dispatch over all scene triangles. Skipped when cards are off (A/B re-shade path). ===
        if (useCards && scene.TotalTriangles > 0)
            LightCards(ctx, sunDirN, clusteredLights, ibl, skyIntensity, useSky);

        *(LumenConstants*)traceCbMapped = new LumenConstants
        {
            InvViewProj = Matrix4x4.Transpose(invVP),
            ViewProj = Matrix4x4.Transpose(ctx.ViewProj),
            CameraPos = ctx.CamPos, Intensity = intensity,
            TexelSize = new Vector2(1f / indirect.Width, 1f / indirect.Height),
            RayCount = rayCount, FrameIndex = ctx.DeterministicCapture ? 0f : frameCounter,
            NormalBias = EnvF("BALLISTIC_DX12_LUMEN_NORMALBIAS", 0.03f), MaxRayDist = maxDist, UseCards = useCards ? 1f : 0f, ScreenSteps = 16f,
            SkyIntensity = skyIntensity, UseSky = useSky ? 1f : 0f,
            // Screen-trace DEFAULT OFF. It contributed a "ghost of another geometry" smudge that's static (not
            // temporal) and GI-only: a screen-trace hit returns the hit pixel's full LIT SceneColor (albedo
            // INCLUDED), so (a) it double-counts albedo vs the RT cards path (which returns albedo-free irradiance
            // the combine multiplies), and (b) on a thin silhouette its thickness window mis-hits the FOREGROUND
            // geometry behind/beside it, painting that surface's colour/shadow onto the edge. The RT trace already
            // owns near+mid+far GI correctly and view-independently, so screen-trace's only role (a cheap contact
            // bounce) isn't worth the artifact. Re-enable with BALLISTIC_DX12_LUMEN_SCREEN=1.
            UseScreenTrace = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_SCREEN") == "1" ? 1f : 0f,
            // Short confident-contact range for the screen trace; mid/far GI is RT (view-independent). The old
            // behaviour let ANY on-screen hit veto RT → view-dependent darkening when the light source panned off.
            ScreenRange = EnvF("BALLISTIC_DX12_LUMEN_SCREEN_RANGE", 1.5f),
            // #3 probe temporal accumulation. HistoryValid 0 on the first frame / after a resize → take raw E.
            // ProbeAlpha = this-frame weight in the EMA (lower = smoother + more lag). A deterministic capture KEEPS
            // accumulation (a fixed frame means a fixed, reproducible accumulation over the static camera — and the
            // accumulated result is the CLEAN one we want to measure, not a single noisy frame).
            HistoryValid = probeHistoryValid ? 1f : 0f,
            ProbeAlpha = EnvF("BALLISTIC_DX12_LUMEN_PROBE_ALPHA", 0.05f),   // 0.05 = strong temporal accumulation (kills per-ray sparkle); the soft-trust blend handles motion so a low base alpha no longer causes gitgel
            // #4 importance sampling: guarantee a sun-facing ray. DEFAULT OFF — measured on the GI Test scene it
            // did NOT help and slightly HURT at 1 ray (10.8% -> 12.7% hotspot): that scene's dominant indirect is
            // sky-ambient + many point/spot bounces, not the sun, so spending a ray on the sun direction missed the
            // real contributors. Correct importance needs the actual radiance distribution (octahedral probes /
            // ReSTIR) — too big a lift for the marginal gain now that temporal accumulation already cleans the
            // grain. Kept opt-in (BALLISTIC_DX12_LUMEN_IMPORTANCE=1) for genuinely sun-dominant scenes.
            ImportanceSampling = EnvF("BALLISTIC_DX12_LUMEN_IMPORTANCE", 0f),
            TexelDim = scene.TexelDim,
            PrevViewProj = Matrix4x4.Transpose(ctx.PrevViewProjUnjittered),   // world → prev clip (HLSL column-major)
        };
        *(LumenSun*)sunCbMapped = new LumenSun
        {
            SunDir = sunDirN, SunBias = 0.03f, SunColor = ctx.LightColor, LightCount = clusteredLights.LightCount,
        };

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Dx12DescriptorHeap bindless = Dx12Backend.BindlessHeap;
        // TLAS is a ROOT SRV (bound below); the table holds t1-t6 + u0 in the reserved tail (so the one bound
        // CBV/SRV/UAV heap serves both the table AND the closest-hit's ResourceDescriptorHeap[] bindless reads).
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(LumenTableBase + 0), gbuffer.DepthSrvCpu, heapType);     // t1 depth
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(LumenTableBase + 1), gbuffer.ColorSrvCpu(1), heapType);  // t2 world normal
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(LumenTableBase + 2), gbuffer.ColorSrvCpu(2), heapType);  // t3 material
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(LumenTableBase + 3), target.ColorSrvCpu, heapType);      // t4 lit scene color
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(LumenTableBase + 4), ibl.IrradianceSrv, heapType);       // t5 sky irradiance
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(LumenTableBase + 5), ibl.PrefilterSrv, heapType);        // t6 sky prefilter
        dev.Device.CreateUnorderedAccessView(indirect.RenderTarget, null, new UnorderedAccessViewDescription
        {
            Format = Dx12OffscreenTarget.HdrFormat, ViewDimension = UnorderedAccessViewDimension.Texture2D,
        }, bindless.Cpu(LumenTableBase + 6));                                                                      // u0 indirect
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(LumenTableBase + 7), probeHistory.ColorSrvCpu, heapType);  // t14 ProbeHistory (#3)
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(LumenTableBase + 8), gbuffer.ColorSrvCpu(4), heapType);    // t15 motion (ghosting reject)

        // The trace reads depth/normal/material/scene-color as SRVs from the COMPUTE (non-pixel) stage, and the
        // scene color must be readable too. Promote: G-buffer to the combined read; scene color to SRV. ALL in
        // one list so state tracking is exact (the RTAO-pass pattern that avoided the split-submit barrier bugs).
        gbuffer.ToShaderResource();
        target.ColorToShaderResource();
        probeHistory.ColorToShaderResource();   // #3: the trace reads last frame's accumulated probes (table t14)
        indirect.ColorToUnorderedAccess();

        if (WantScreenProbe(ctx))
        {
            // Sıra 1: SCREEN-PROBE front end fills `indirect` (place → trace → integrate). Same E contract.
            TraceScreenProbe(ctx, sceneAS, rtGeo, clusteredLights, intensity, maxDist, skyIntensity, useSky, useCards);
        }
        else
        {
            dev.ExecuteSync(cl =>
            {
                cl.SetDescriptorHeaps(bindless.Heap);
                cl.SetComputeRootSignature(traceRootSig);
                cl.SetPipelineState(tracePso);
                cl.SetComputeRootConstantBufferView(0, traceCb.GPUVirtualAddress);
                cl.SetComputeRootConstantBufferView(1, sunCb.GPUVirtualAddress);
                cl.SetComputeRootShaderResourceView(2, sceneAS.TlasAddress);                  // t0 TLAS (root SRV)
                cl.SetComputeRootDescriptorTable(3, bindless.Gpu(LumenTableBase));            // t1-t6 + u0
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
        indirect.ColorToShaderResource();

        // #3: snapshot this frame's accumulated probes (indirect, with depth in .a) into the history for next
        // frame's EMA. Must happen BEFORE the denoise overwrites indirect's .a. After this, indirect's rgb feeds
        // the denoise/combine as before (the .a depth is ignored downstream).
        probeHistory.CopyColorFrom(indirect);
        probeHistory.ColorToShaderResource();
        probeHistoryValid = true;

        // === SPATIAL DENOISE: edge-aware à-trous blur of the raw indirect E (diffuse GI is low-frequency, so a
        // wide bilateral blur removes the per-pixel hemisphere-ray grain without a screen-space temporal
        // history). 3 iterations at increasing stride (1,2,4) ping-ponging indirect↔indirectFiltered → an
        // effective ~33px footprint. The LAST written buffer is indirectFiltered (the combine reads it).
        // BALLISTIC_DX12_LUMEN_NODENOISE=1 passes through (raw E). ===
        bool denoise = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_NODENOISE") != "1" && fx.LumenDenoisePasses > 0;
        int dnPasses = denoise ? Math.Clamp((int)EnvF("BALLISTIC_DX12_LUMEN_DENOISE_PASSES", fx.LumenDenoisePasses), 1, MaxDenoisePasses) : 1;
        Dx12OffscreenTarget src = indirect, dst = indirectFiltered;
        denoiseSrv.Reset();   // ONCE per frame — each pass takes a DISTINCT 4-descriptor range (no cross-pass alias)
        for (int pass = 0; pass < dnPasses; pass++)
        {
            unsafe
            {
                *(DenoiseConstants*)denoiseCbMappedArr[pass] = new DenoiseConstants
                {
                    Texel = new Vector2(1f / indirect.Width, 1f / indirect.Height),
                    Step = denoise ? (1 << pass) : 1f, Enabled = denoise ? 1f : 0f,
                };
            }
            int db = denoiseSrv.AllocateRange(4);
            dev.Device.CopyDescriptorsSimple(1, denoiseSrv.Cpu(db + 0), src.ColorSrvCpu, heapType);           // t0 E in
            dev.Device.CopyDescriptorsSimple(1, denoiseSrv.Cpu(db + 1), gbuffer.DepthSrvCpu, heapType);       // t1 depth
            dev.Device.CopyDescriptorsSimple(1, denoiseSrv.Cpu(db + 2), gbuffer.ColorSrvCpu(1), heapType);    // t2 normal
            dev.Device.CreateUnorderedAccessView(dst.RenderTarget, null, new UnorderedAccessViewDescription
            {
                Format = Dx12OffscreenTarget.HdrFormat, ViewDimension = UnorderedAccessViewDimension.Texture2D,
            }, denoiseSrv.Cpu(db + 3));                                                                        // u0 E out
            dst.ColorToUnorderedAccess();
            ulong passCbAddr = denoiseCbs[pass].GPUVirtualAddress;
            dev.ExecuteSync(cl =>
            {
                cl.SetDescriptorHeaps(denoiseSrv.Heap);
                cl.SetComputeRootSignature(denoiseRootSig);
                cl.SetPipelineState(denoisePso);
                cl.SetComputeRootConstantBufferView(0, passCbAddr);
                cl.SetComputeRootDescriptorTable(1, denoiseSrv.Gpu(db));
                cl.Dispatch((uint)((indirect.Width + 7) / 8), (uint)((indirect.Height + 7) / 8), 1);
            });
            dst.ColorToShaderResource();
            // Ping-pong for the next iteration; ensure the FINAL result lands in indirectFiltered.
            (src, dst) = (dst, src);
        }
        // After the loop `src` holds the last-written result. If that isn't indirectFiltered, the combine must
        // read `src` — but to keep the combine bind stable, copy the result into indirectFiltered when needed.
        if (!ReferenceEquals(src, indirectFiltered))
            indirectFiltered.CopyColorFrom(src);
        indirectFiltered.ColorToShaderResource();

        // === COMBINE: add E*albedo*ao/PI directly into the HDR scene color via an additive (One/One) fullscreen
        // PSO — no scratch target needed. The deferred pass already suppressed its IBL diffuse ambient
        // (ctx.LumenActiveThisFrame → UseIBLDiffuse=0), so this adds Lumen's diffuse indirect without double-count.
        // BALLISTIC_DX12_LUMEN_DEBUG=1 swaps to an OPAQUE-replace PSO that shows the raw irradiance E instead. ===
        gbuffer.ToShaderResource();
        // GTAO into the GI combine at the AmbientOcclusion volume's strength (env override _LUMEN_AO). The GTAO
        // buffer is ctx.AoResult when AO is actually rendered this frame; else a valid fallback + AoStrength 0
        // (so the fallback's contents never affect the output). This is what makes the AmbientOcclusion override
        // drive contact detail in the GI; the RT trace already has macro occlusion so the default strength is
        // partial (no double-darkening of corners).
        // GTAO is NOT mixed into the GI by default. It's a SCREEN-SPACE term, so under camera motion it dragged a
        // dark "ghost of nearby geometry" smudge onto every surface (static per view, GI-only — the reported bug),
        // and the Lumen RT trace ALREADY carries macro occlusion (rays that don't escape find less light), so GTAO
        // on top double-darkened corners anyway. Default strength 0 (the volume's LumenAoStrength / env can opt it
        // back in for a scene that specifically wants the extra contact term and tolerates the screen-space cost).
        bool aoOn = ctx.Doors.Ssao && fx.SSAOEnabled;
        float aoStrength = aoOn ? EnvF("BALLISTIC_DX12_LUMEN_AO", 0f) : 0f;
        *(CombineConstants*)combineCbMapped = new CombineConstants
        {
            AoStrength = aoStrength,
            IndirectTexel = new Vector2(1f / indirect.Width, 1f / indirect.Height),   // half-res texel for the upsample
        };
        combineSrv.Reset();
        int cb = combineSrv.AllocateRange(5);
        dev.Device.CopyDescriptorsSimple(1, combineSrv.Cpu(cb + 0), indirectFiltered.ColorSrvCpu, heapType);  // t0 E (denoised)
        dev.Device.CopyDescriptorsSimple(1, combineSrv.Cpu(cb + 1), gbuffer.ColorSrvCpu(0), heapType);        // t1 albedo
        dev.Device.CopyDescriptorsSimple(1, combineSrv.Cpu(cb + 2), gbuffer.ColorSrvCpu(2), heapType);        // t2 material (baked ao)
        dev.Device.CopyDescriptorsSimple(1, combineSrv.Cpu(cb + 3), gbuffer.DepthSrvCpu, heapType);           // t3 depth
        dev.Device.CopyDescriptorsSimple(1, combineSrv.Cpu(cb + 4), aoOn ? ctx.AoResult : gbuffer.DepthSrvCpu, heapType); // t4 GTAO
        bool debugE = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_DEBUG") == "1";
        ID3D12PipelineState pso = debugE ? combineDebugPso : combinePso;
        target.RenderColorOnly(cl =>
        {
            cl.SetGraphicsRootSignature(combineRootSig);
            cl.SetPipelineState(pso);                  // additive One/One blend (or opaque replace when debugE)
            cl.SetDescriptorHeaps(combineSrv.Heap);
            cl.SetGraphicsRootConstantBufferView(0, combineCb.GPUVirtualAddress);
            cl.SetGraphicsRootDescriptorTable(1, combineSrv.Gpu(cb));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });

        // Swap the cache ping-pong: this frame's written cache becomes next frame's "previous" (EMA + bounce
        // source). Only when cards actually ran this frame (else the read/write buffers didn't advance).
        if (useCards && scene.TotalTriangles > 0)
            scene.SwapCache();
        frameCounter++;
    }

    // P3 card lighting: 1D dispatch over all scene triangles, writing each triangle's lit first-bounce radiance
    // into scene.CardRadiance. Reads the shared TLAS (shadow rays) + bindless geo/material + the per-instance
    // meta. The trace then samples these cards on RT hits (no per-hit relighting).
    unsafe void LightCards(Dx12FrameContext ctx, Vector3 sunDir, Dx12ClusteredLights clusteredLights,
                           Dx12IblBaker ibl, float skyIntensity, bool useSky)
    {
        var sceneAS = ctx.Dxr.SceneAS;
        float emaAlpha = EnvF("BALLISTIC_DX12_LUMEN_EMA", 0.1f);          // conservative temporal blend (0.1 = slow, stable)
        bool bounce = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_NOBOUNCE") != "1" && ctx.PostFX.LumenMultiBounce;

        // === P7 #1 UPDATE BUDGET ===
        // Re-light only a round-robin slice of records each frame instead of the whole scene. stride = how many
        // frames a full sweep takes; a record relights every `stride`-th frame. budget = target records/frame
        // (the door BALLISTIC_DX12_LUMEN_BUDGET overrides; 0 = unlimited → stride 1 = old behaviour). The EMA
        // makes a strided update visually identical to a per-frame one for a STATIC light. Small scenes
        // (tris ≤ budget) get stride 1 automatically (no change). Determinism: a deterministic capture forces
        // stride 1 (a strided cache depends on frame count → not byte-reproducible).
        int budget = (int)EnvF("BALLISTIC_DX12_LUMEN_BUDGET", 200000f);
        int tris = scene.RecordCount;   // budget now counts RECORDS (clusters), the card-light dispatch unit
        uint stride = 1u;
        // A deterministic capture renders a FIXED frame, so the round-robin phase (FrameIndex % stride) is itself
        // deterministic → byte-identical across runs. Hence budget is safe under DeterministicCapture (it does NOT
        // disable it like the EMA does), so `bal perf` measures the real budgeted cost.
        if (budget > 0 && tris > budget)
            stride = (uint)Math.Min(8, (tris + budget - 1) / budget);   // cap at 8 → at most ~8-frame react latency

        // ForceFull this frame when the light state changed (or topology rebuilt) so the budget never delays a
        // light change. Compared against the values the cache was last fully relit with.
        bool lightChanged = float.IsNaN(prevSunDir.X)
            || Vector3.DistanceSquared(prevSunDir, sunDir) > 1e-8f
            || Vector3.DistanceSquared(prevSunColor, ctx.LightColor) > 1e-6f
            || prevLightCount != clusteredLights.LightCount
            || scene.DirtyThisFrame;
        uint forceFull = lightChanged ? 1u : 0u;
        if (lightChanged) { prevSunDir = sunDir; prevSunColor = ctx.LightColor; prevLightCount = clusteredLights.LightCount; }

        *(LumenCardConstants*)cardCbMapped = new LumenCardConstants
        {
            SunDir = sunDir, SunBias = 0.03f, SunColor = ctx.LightColor, LightCount = clusteredLights.LightCount,
            InstanceCount = (uint)scene.InstanceCount, TotalTris = (uint)scene.RecordCount,   // #2A: dispatch bound = record count
            SkyIntensity = skyIntensity, UseSky = useSky ? 1f : 0f, SkyVisRays = 4f,
            EmaAlpha = emaAlpha, BounceRays = bounce ? 4f : 0f,
            HistoryValid = (scene.HistoryValid && !ctx.DeterministicCapture) ? 1f : 0f,
            FrameIndex = (uint)frameCounter, UpdateStride = stride, ForceFull = forceFull,
            TexelDim = (uint)scene.TexelDim,
        };

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
            cl.SetComputeRootConstantBufferView(0, cardCb.GPUVirtualAddress);
            cl.SetComputeRootShaderResourceView(1, sceneAS.TlasAddress);                 // t0 TLAS
            cl.SetComputeRootUnorderedAccessView(2, scene.CardRadianceWriteGpu);         // u0 CardRadiance (write)
            cl.SetComputeRootDescriptorTable(3, bindless.Gpu(CardSkyTableBase));         // t1 sky cube
            cl.SetComputeRootShaderResourceView(4, scene.InstanceMetaGpuAddress);        // t2 LumenInstanceMeta
            cl.SetComputeRootShaderResourceView(5, ctx.Dxr.RtGeometry.InstancesGpuAddress); // t3 RtInstance[]
            cl.SetComputeRootShaderResourceView(6, ctx.GpuDriven.MaterialsGpuAddress);   // t4 GpuMaterials
            cl.SetComputeRootShaderResourceView(7, clusteredLights.LightBufGpuAddress);  // t5 Lights
            cl.SetComputeRootShaderResourceView(8, scene.CardRadianceReadGpu);           // t6 PrevCard (read)
            cl.SetComputeRootUnorderedAccessView(9, scene.LastUpdatedGpu);               // u1 LastUpdated (age)
            cl.SetComputeRootShaderResourceView(10, scene.TriToClusterGpuAddress);       // t7 TriToCluster
            cl.SetComputeRootShaderResourceView(11, scene.ClusterToTriGpuAddress);       // t8 ClusterToTri
            cl.SetComputeRootShaderResourceView(12, scene.ClusterCardsGpuAddress);       // t9 ClusterCards (Sıra 5)
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

        *(ProbeConstants*)spProbeCbMapped = new ProbeConstants
        {
            InvViewProj = Matrix4x4.Transpose(invVP),
            ViewProj = Matrix4x4.Transpose(ctx.ViewProj),
            CameraPos = ctx.CamPos, Intensity = intensity,
            FullTexel = new Vector2(1f / indirect.Width, 1f / indirect.Height),
            RayCount = octSize * octSize, FrameIndex = ctx.DeterministicCapture ? 0f : frameCounter,
            NormalBias = 0.03f, MaxRayDist = maxDist, UseCards = useCards ? 1f : 0f, ScreenSteps = 16f,
            SkyIntensity = skyIntensity, UseSky = useSky ? 1f : 0f,
            UseScreenTrace = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_NOSCREEN") == "1" ? 0f : 1f,
            ScreenRange = EnvF("BALLISTIC_DX12_LUMEN_SCREEN_RANGE", 1.5f),
            FalloffDist = EnvF("BALLISTIC_DX12_LUMEN_FALLOFF", 12f),
            ProbeTile = 0f, ProbeStride = probeStride, OctSize = octSize,
            ProbesX = (uint)probesX, ProbesY = (uint)probesY,
            FullW = (uint)indirect.Width, FullH = (uint)indirect.Height,
            // Sıra 3 temporal accumulation. A deterministic capture KEEPS accumulation (fixed frame → fixed,
            // reproducible accumulation over the static camera — the converged result is what we measure).
            HistoryValid = spHistoryValid ? 1f : 0f,
            ProbeEma = EnvF("BALLISTIC_DX12_LUMEN_PROBE_EMA", 0.1f),   // this-frame weight; low = strong accumulation
            TexelDim = scene.TexelDim,
        };
        *(LumenSun*)spSunCbMapped = new LumenSun
        {
            SunDir = sunDirN, SunBias = 0.03f, SunColor = ctx.LightColor, LightCount = clusteredLights.LightCount,
        };

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Dx12DescriptorHeap bindless = Dx12Backend.BindlessHeap;
        // Table t1-t6 SRV + u1 atlas UAV in the screen-probe reserved tail.
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
        // u2 indirect (SpTableBase+7) + t13 atlas history (SpTableBase+8) — ALL in the ONE bindless heap so a single
        // SetDescriptorHeaps(bindless) covers every table (a separate heap for these caused SetDescriptorTableInvalid).
        dev.Device.CreateUnorderedAccessView(indirect.RenderTarget, null, new UnorderedAccessViewDescription
        {
            Format = Dx12OffscreenTarget.HdrFormat, ViewDimension = UnorderedAccessViewDimension.Texture2D,
        }, bindless.Cpu(SpTableBase + 7));                                                                      // u2 indirect
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(SpTableBase + 8), probeAtlasHistory.ColorSrvCpu, heapType); // t13 atlas history

        probeAtlas.ColorToUnorderedAccess();

        void SetCommonRoots(ID3D12GraphicsCommandList cl)
        {
            cl.SetComputeRootConstantBufferView(0, spProbeCb.GPUVirtualAddress);
            cl.SetComputeRootConstantBufferView(1, spSunCb.GPUVirtualAddress);
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
        }

        // Root slot 11 table = u2 indirect (SpTableBase+7) + t13 atlas history (SpTableBase+8), bound from the
        // SAME bindless heap as slot 3. The descriptors are written ONCE above (not per-dispatch) → no
        // StaticDescriptorInvalidDescriptorChange; one SetDescriptorHeaps(bindless) serves all tables → no
        // SetDescriptorTableInvalid. The 3 dispatches are separate ExecuteSync; GPU work is serialised by each
        // submit's WaitForGpu so no inter-dispatch UAV barrier is needed.
        var slot11 = bindless.Gpu(SpTableBase + 7);

        // PLACE — writes probeHeaders (root UAV). indirect + atlas already UAV.
        dev.ExecuteSync(cl =>
        {
            cl.SetDescriptorHeaps(bindless.Heap);
            cl.SetComputeRootSignature(spRootSig);
            cl.SetPipelineState(spPlacePso);
            SetCommonRoots(cl);
            cl.SetComputeRootDescriptorTable(11, slot11);
            cl.Dispatch((uint)((probesX + 7) / 8), (uint)((probesY + 7) / 8), 1);
        });

        // TRACE — reads the previous accumulated atlas (t13) from a COMPUTE shader → NON_PIXEL state (not the
        // default PIXEL from ColorToShaderResource; that PIXEL/NON_PIXEL mismatch was a GBV InvalidSubresourceState).
        probeAtlasHistory.ColorToNonPixelShaderResource();
        dev.ExecuteSync(cl =>
        {
            cl.SetDescriptorHeaps(bindless.Heap);
            cl.SetComputeRootSignature(spRootSig);
            cl.SetPipelineState(spTracePso);
            SetCommonRoots(cl);
            cl.SetComputeRootDescriptorTable(11, slot11);
            cl.Dispatch((uint)((probesX * octSize + 7) / 8), (uint)((probesY * octSize + 7) / 8), 1);
        });

        // Snapshot this frame's accumulated atlas + headers into the history for next frame's EMA + reproject test.
        probeAtlasHistory.CopyColorFrom(probeAtlas);
        // headers → prev (a plain buffer copy; both are committed UAV/SRV-readable structured buffers).
        dev.ExecuteSync(cl =>
        {
            cl.ResourceBarrierTransition(probeHeaders, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
            cl.ResourceBarrierTransition(probeHeadersPrev, ResourceStates.GenericRead, ResourceStates.CopyDest);
            cl.CopyResource(probeHeadersPrev, probeHeaders);
            cl.ResourceBarrierTransition(probeHeaders, ResourceStates.CopySource, ResourceStates.UnorderedAccess);
            cl.ResourceBarrierTransition(probeHeadersPrev, ResourceStates.CopyDest, ResourceStates.GenericRead);
        });
        spHistoryValid = true;

        // INTEGRATE — reads the atlas as a UAV (RWTexture2D) + the history SRV (NON_PIXEL). Bring the atlas back to
        // UAV (CopyColorFrom left it COPY_SOURCE). The t13 history descriptor was written once → still valid.
        probeAtlas.ColorToUnorderedAccess();
        dev.ExecuteSync(cl =>
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

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("LumenGi.hlsl");
        byte[] cs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSTrace", "LumenGi.hlsl");
        tracePso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = traceRootSig, ComputeShader = cs,
        });

        int cbSize = (Marshal.SizeOf<LumenConstants>() + 255) & ~255;
        traceCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        traceCbMapped = traceCb.Map<byte>(0);
        int sunSize = (Marshal.SizeOf<LumenSun>() + 255) & ~255;
        sunCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)sunSize), ResourceStates.GenericRead);
        sunCbMapped = sunCb.Map<byte>(0);

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

        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSCombine", "LumenGi.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSCombine", "LumenGi.hlsl");
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
        byte[] psDebug = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSDebugE", "LumenGi.hlsl");
        combineDebugPso = dev.Device.CreateGraphicsPipelineState(MakeCombine(psDebug, BlendDescription.Opaque));

        combineSrv = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 10, shaderVisible: true, framesInFlight: dev.FramesInFlight);
        int combCbSize = (Marshal.SizeOf<CombineConstants>() + 255) & ~255;
        combineCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)combCbSize), ResourceStates.GenericRead);
        combineCbMapped = combineCb.Map<byte>(0);

        BuildCardPipeline();
    }

    // Card-lighting compute (LumenCardLight.hlsl): TLAS t0 (root SRV) | CardRadiance u0 (root UAV) | sky cube
    // t1 (table, in the bindless tail) | LumenInstanceMeta t2 / RtInstance[] t3 / GpuMaterials t4 / Lights t5
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
                new[] { cbv0, tlasSrv, uavRoot, skyTable, instMeta, rtInst, mats, lights, prevCard, ageUav, triClus, clusTri, clusCards }, new[] { clamp, wrap })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("LumenCardLight.hlsl");
        byte[] cs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSMain", "LumenCardLight.hlsl");
        cardPso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription { RootSignature = cardRootSig, ComputeShader = cs });

        int cbSize = (Marshal.SizeOf<LumenCardConstants>() + 255) & ~255;
        cardCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        cardCbMapped = cardCb.Map<byte>(0);

        BuildDenoisePipeline();
    }

    // Spatial-denoise compute (LumenGi.hlsl CSDenoise): CBV b0 + table{E t0 / depth t1 / normal t2 SRV, u0 UAV}.
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

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("LumenGi.hlsl");
        byte[] cs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSDenoise", "LumenGi.hlsl");
        denoisePso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription { RootSignature = denoiseRootSig, ComputeShader = cs });

        // PER-PASS CBs (not one shared CB): the denoise runs up to MaxDenoisePasses iterations recorded into ONE
        // pipelined-frame command list, so a single CB would have every pass read the LAST pass's stride (and a
        // single 4-descriptor heap Reset per pass would alias every pass's descriptors → only the last survived,
        // the rest read garbage → the GI went BLACK at passes >= 4). One CB + one 4-descriptor range PER PASS.
        int cbSize = (Marshal.SizeOf<DenoiseConstants>() + 255) & ~255;
        denoiseCbs = new ID3D12Resource[MaxDenoisePasses];
        denoiseCbMappedArr = new System.IntPtr[MaxDenoisePasses];
        for (int i = 0; i < MaxDenoisePasses; i++)
        {
            denoiseCbs[i] = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
                ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
            unsafe { denoiseCbMappedArr[i] = (System.IntPtr)denoiseCbs[i].Map<byte>(0); }
        }
        denoiseSrv = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, MaxDenoisePasses * 4,
            shaderVisible: true, framesInFlight: dev.FramesInFlight);

        BuildScreenProbePipeline();
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
        var indirectTable = new RootParameter1(new RootDescriptorTable1(indirectUavRange, atlasHistRange), ShaderVisibility.All);
        var prevHeaderSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(16, 0), ShaderVisibility.All);  // t16 prev headers (root SRV, buffer)
        var spCardsSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(17, 0), ShaderVisibility.All);  // t17 ClusterCards (Sıra 5)
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
        // Roots: 0 cbv0,1 cbv1,2 t0 TLAS,3 table{t1-t6,u1},4-9 t7-t12,10 u0 headers,11 table{u2,t13 hist},12 t16 prev,13 t17 cards.
        spRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(
                RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
                new[] { cbv0, cbv1, tlasSrv, table, matSrv, instSrv, lightSrv, cardSrv, metaSrv, triClusterSrv, headerUav, indirectTable, prevHeaderSrv, spCardsSrv },
                new[] { clampSamp, wrapSamp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("LumenScreenProbe.hlsl");
        spPlacePso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = spRootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSPlace", "LumenScreenProbe.hlsl"),
        });
        spTracePso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = spRootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSProbeTrace", "LumenScreenProbe.hlsl"),
        });
        spIntegratePso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = spRootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSIntegrate", "LumenScreenProbe.hlsl"),
        });

        int pcSize = (Marshal.SizeOf<ProbeConstants>() + 255) & ~255;
        spProbeCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)pcSize), ResourceStates.GenericRead);
        spProbeCbMapped = spProbeCb.Map<byte>(0);
        int snSize = (Marshal.SizeOf<LumenSun>() + 255) & ~255;
        spSunCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)snSize), ResourceStates.GenericRead);
        spSunCbMapped = spSunCb.Map<byte>(0);
        // (No separate descriptor heap: u2 indirect + t13 atlas history live in the ONE bindless heap at the
        // screen-probe tail — a second shader-visible heap can't be bound simultaneously.)
    }

    const int MaxDenoisePasses = 5;
    ID3D12Resource[] denoiseCbs;
    System.IntPtr[] denoiseCbMappedArr;

    // P7 #1b — the indirect E (trace) + the denoise scratch run at HALF render resolution (the dominant cost in
    // the baseline was this geometry-independent full-res trace+denoise floor, ~1.2ms; diffuse indirect is
    // low-frequency so half-res is visually free with a depth-aware upsample in the combine). The combine still
    // reads the FULL-res G-buffer (albedo/depth/AO) and depth-aware-upsamples the half-res E. fullW/fullH are
    // kept so the combine knows the upsample ratio. BALLISTIC_DX12_LUMEN_RESSCALE overrides (1 = full-res A/B,
    // 2 = half (default), 4 = quarter). Committed (cross-pass scratch; never pooled).
    int fullW, fullH;
    public void Resize(int w, int h)
    {
        fullW = Math.Max(1, w); fullH = Math.Max(1, h);
        // Default FULL-res. Measured on RX 9070 XT: half/quarter-res gave NO perf win (Lumen cost here is RT-
        // traversal/dispatch-bound, not pixel-bound) but DID cost quality (Cornell/Bistro hotspot ~5-8%). So the
        // scale stays opt-in (BALLISTIC_DX12_LUMEN_RESSCALE=2/4) for 4K / weak-GPU cases where pixel count bites;
        // the depth-aware upsample + UV-sampled trace/denoise are kept so it's correct when enabled.
        // #3 PROBE: the trace accumulates temporally. Default scale = 1 (FULL-res) — measured: half-res probes left
        // a visible 2x2 'kare' block sparkle at higher GI intensity, and the cost is RT-traversal-bound (pixel
        // count doesn't move the frame time here), so full-res is free AND cleaner. The temporal accumulation is
        // what actually kills the per-ray sparkle. RESSCALE=2/4 stays available for weak-GPU / 4K. The depth-aware
        // combine still upsamples when scale > 1.
        int scale = Math.Clamp((int)EnvF("BALLISTIC_DX12_LUMEN_RESSCALE", 1f), 1, 4);
        int lw = Math.Max(1, fullW / scale), lh = Math.Max(1, fullH / scale);
        indirect?.Dispose();
        indirectFiltered?.Dispose();
        probeHistory?.Dispose();
        indirect = new Dx12OffscreenTarget(dev, lw, lh, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: true);
        indirectFiltered = new Dx12OffscreenTarget(dev, lw, lh, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: true);
        probeHistory = new Dx12OffscreenTarget(dev, lw, lh, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: true);
        probeHistoryValid = false;   // a resized history is stale → first frame takes the raw E (alpha=1)

        // Sıra 1 — screen-probe grid + octahedral atlas, sized off the `indirect` resolution (the GI front-end
        // resolution). One probe per probeStride×probeStride tile; each probe holds an octSize×octSize oct tile.
        // Default stride 24 (tuned): vs 16 it is CHEAPER (Bistro GI 3.72→3.31ms, ~11%) AND slightly SMOOTHER
        // (more full-res pixels averaged per probe → lower variance: Bistro grain 0.504→0.478, SunTemple 0.262→
        // 0.251), with identical mean/coverage. Larger strides start to blob on dense thin geometry; 24 is the
        // measured sweet spot. BALLISTIC_DX12_LUMEN_PROBE_STRIDE overrides.
        probeStride = Math.Clamp((int)EnvF("BALLISTIC_DX12_LUMEN_PROBE_STRIDE", 24f), 4, 64);
        octSize = Math.Clamp((int)EnvF("BALLISTIC_DX12_LUMEN_PROBE_OCT", 8f), 4, 16);
        probesX = (lw + probeStride - 1) / probeStride;
        probesY = (lh + probeStride - 1) / probeStride;
        probeHeaderCount = Math.Max(probesX * probesY, 1);
        probeHeaders?.Dispose();
        // ProbeHeader = 2× float4 = 32 bytes. UAV structured buffer (root UAV).
        probeHeaders = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(probeHeaderCount * 32), ResourceFlags.AllowUnorderedAccess),
            ResourceStates.UnorderedAccess);
        probeHeadersPrev?.Dispose();   // Sıra 3: previous-frame headers (reproject reject) — copy dest / root SRV
        probeHeadersPrev = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(probeHeaderCount * 32)), ResourceStates.GenericRead);
        probeAtlas?.Dispose();
        probeAtlas = new Dx12OffscreenTarget(dev, Math.Max(probesX * octSize, 1), Math.Max(probesY * octSize, 1),
            withDepth: false, colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: true);
        probeAtlasHistory?.Dispose();   // Sıra 3: previous-frame accumulated atlas (EMA source)
        probeAtlasHistory = new Dx12OffscreenTarget(dev, Math.Max(probesX * octSize, 1), Math.Max(probesY * octSize, 1),
            withDepth: false, colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: true);
        spHistoryValid = false;   // a resized atlas history is stale → first frame takes the raw trace (alpha=1)
    }

    public void Dispose()
    {
        scene.Dispose();
        tracePso?.Dispose(); traceRootSig?.Dispose(); traceCb?.Dispose(); sunCb?.Dispose();
        cardPso?.Dispose(); cardRootSig?.Dispose(); cardCb?.Dispose();
        denoisePso?.Dispose(); denoiseRootSig?.Dispose(); denoiseSrv?.Dispose();
        if (denoiseCbs != null) foreach (var cb in denoiseCbs) cb?.Dispose();
        combinePso?.Dispose(); combineDebugPso?.Dispose(); combineRootSig?.Dispose(); combineSrv?.Dispose(); combineCb?.Dispose();
        indirect?.Dispose(); indirectFiltered?.Dispose(); probeHistory?.Dispose();
        spPlacePso?.Dispose(); spTracePso?.Dispose(); spIntegratePso?.Dispose(); spRootSig?.Dispose();
        spProbeCb?.Dispose(); spSunCb?.Dispose();
        probeHeaders?.Dispose(); probeHeadersPrev?.Dispose(); probeAtlas?.Dispose(); probeAtlasHistory?.Dispose();
    }
}
