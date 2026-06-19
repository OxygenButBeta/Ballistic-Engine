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

    // The product door. BALLISTIC_DX12_LUMEN=1 arms Lumen; unset/0 = off (no substrate alloc, no-op Record).
    int? armed;
    public bool Armed => (armed ??= Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN") == "1" ? 1 : 0) == 1;

    // The frame-level "Lumen runs" predicate, shared with the orchestrator (which mirrors it into
    // ctx.LumenActiveThisFrame so the deferred pass suppresses its IBL diffuse ambient before this pass adds
    // its own diffuse indirect). Lumen is HW-RT only — no hidden SSGI fallback (plan gate #6).
    public static bool WouldRun(Dx12FrameContext ctx, bool armed) =>
        !ctx.Doors.Minimal && armed && ctx.Dev.HasHardwareRayTracing && ctx.Dxr?.SceneAS != null;

    public bool Enabled(Dx12FrameContext ctx) => WouldRun(ctx, Armed);

    // ---- trace (inline RayQuery compute) ----
    ID3D12RootSignature traceRootSig;   // HeapDirectlyIndexed; CBV b0/b1 + table{t0-t6, u0} + root SRV t7/t8/t9 + s0/s1
    ID3D12PipelineState tracePso;
    ID3D12Resource traceCb, sunCb;
    unsafe byte* traceCbMapped, sunCbMapped;
    const int LumenTableBase = Dx12BindlessTail.LumenTableBase;
    Dx12OffscreenTarget indirect;       // full-res RGBA16F incoming irradiance E (cross-pass scratch; rebuilt on resize)

    // ---- spatial denoise (edge-aware blur of the per-pixel indirect E) ----
    ID3D12RootSignature denoiseRootSig; // CBV b0 + table{t0-t2 SRV, u0 UAV}
    ID3D12PipelineState denoisePso;
    ID3D12Resource denoiseCb;
    unsafe byte* denoiseCbMapped;
    Dx12DescriptorHeap denoiseSrv;      // 4 descriptors (E/depth/normal SRV + filtered UAV)
    Dx12OffscreenTarget indirectFiltered; // full-res filtered E the combine reads

    [StructLayout(LayoutKind.Sequential)]
    struct DenoiseConstants { public Vector2 Texel; public float Step; public float Enabled; }

    // ---- combine (additive fullscreen) ----
    ID3D12RootSignature combineRootSig; // 4-SRV table + sampler
    ID3D12PipelineState combinePso;
    ID3D12PipelineState combineDebugPso; // OPAQUE replace — BALLISTIC_DX12_LUMEN_DEBUG=1 shows raw E (no add)
    Dx12DescriptorHeap combineSrv;      // 4 SRVs per pass

    [StructLayout(LayoutKind.Sequential)]
    struct LumenConstants
    {
        public Matrix4x4 InvViewProj;
        public Matrix4x4 ViewProj;
        public Vector3 CameraPos; public float Intensity;
        public Vector2 TexelSize; public float RayCount; public float FrameIndex;
        public float NormalBias; public float MaxRayDist; public float UseCards; public float ScreenSteps;
        public float SkyIntensity; public float UseSky; public float Pad0; public float Pad1;
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
    }

    int frameCounter;

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

        float intensity = EnvF("BALLISTIC_DX12_LUMEN_INTENSITY", 1f);
        float rayCount = MathF.Round(EnvF("BALLISTIC_DX12_LUMEN_RAYS", 6f));   // 6 hemisphere rays/px (denoise + cache clean the rest)
        float maxDist = EnvF("BALLISTIC_DX12_LUMEN_DIST", 40f);
        float skyIntensity = EnvF("BALLISTIC_DX12_LUMEN_SKY", 1f);
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
            NormalBias = 0.03f, MaxRayDist = maxDist, UseCards = useCards ? 1f : 0f, ScreenSteps = 16f,
            SkyIntensity = skyIntensity, UseSky = useSky ? 1f : 0f,
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

        // The trace reads depth/normal/material/scene-color as SRVs from the COMPUTE (non-pixel) stage, and the
        // scene color must be readable too. Promote: G-buffer to the combined read; scene color to SRV. ALL in
        // one list so state tracking is exact (the RTAO-pass pattern that avoided the split-submit barrier bugs).
        gbuffer.ToShaderResource();
        target.ColorToShaderResource();
        indirect.ColorToUnorderedAccess();

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
            cl.Dispatch((uint)((indirect.Width + 7) / 8), (uint)((indirect.Height + 7) / 8), 1);
        });
        indirect.ColorToShaderResource();

        // === SPATIAL DENOISE: edge-aware à-trous blur of the raw indirect E (diffuse GI is low-frequency, so a
        // wide bilateral blur removes the per-pixel hemisphere-ray grain without a screen-space temporal
        // history). 3 iterations at increasing stride (1,2,4) ping-ponging indirect↔indirectFiltered → an
        // effective ~33px footprint. The LAST written buffer is indirectFiltered (the combine reads it).
        // BALLISTIC_DX12_LUMEN_NODENOISE=1 passes through (raw E). ===
        bool denoise = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_NODENOISE") != "1";
        int dnPasses = denoise ? Math.Clamp((int)EnvF("BALLISTIC_DX12_LUMEN_DENOISE_PASSES", 3f), 1, 5) : 1;
        Dx12OffscreenTarget src = indirect, dst = indirectFiltered;
        for (int pass = 0; pass < dnPasses; pass++)
        {
            *(DenoiseConstants*)denoiseCbMapped = new DenoiseConstants
            {
                Texel = new Vector2(1f / indirect.Width, 1f / indirect.Height),
                Step = denoise ? (1 << pass) : 1f, Enabled = denoise ? 1f : 0f,
            };
            denoiseSrv.Reset();
            int db = denoiseSrv.AllocateRange(4);
            dev.Device.CopyDescriptorsSimple(1, denoiseSrv.Cpu(db + 0), src.ColorSrvCpu, heapType);           // t0 E in
            dev.Device.CopyDescriptorsSimple(1, denoiseSrv.Cpu(db + 1), gbuffer.DepthSrvCpu, heapType);       // t1 depth
            dev.Device.CopyDescriptorsSimple(1, denoiseSrv.Cpu(db + 2), gbuffer.ColorSrvCpu(1), heapType);    // t2 normal
            dev.Device.CreateUnorderedAccessView(dst.RenderTarget, null, new UnorderedAccessViewDescription
            {
                Format = Dx12OffscreenTarget.HdrFormat, ViewDimension = UnorderedAccessViewDimension.Texture2D,
            }, denoiseSrv.Cpu(db + 3));                                                                        // u0 E out
            dst.ColorToUnorderedAccess();
            dev.ExecuteSync(cl =>
            {
                cl.SetDescriptorHeaps(denoiseSrv.Heap);
                cl.SetComputeRootSignature(denoiseRootSig);
                cl.SetPipelineState(denoisePso);
                cl.SetComputeRootConstantBufferView(0, denoiseCb.GPUVirtualAddress);
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
        combineSrv.Reset();
        int cb = combineSrv.AllocateRange(4);
        dev.Device.CopyDescriptorsSimple(1, combineSrv.Cpu(cb + 0), indirectFiltered.ColorSrvCpu, heapType);  // t0 E (denoised)
        dev.Device.CopyDescriptorsSimple(1, combineSrv.Cpu(cb + 1), gbuffer.ColorSrvCpu(0), heapType);        // t1 albedo
        dev.Device.CopyDescriptorsSimple(1, combineSrv.Cpu(cb + 2), gbuffer.ColorSrvCpu(2), heapType);        // t2 material (ao)
        dev.Device.CopyDescriptorsSimple(1, combineSrv.Cpu(cb + 3), gbuffer.DepthSrvCpu, heapType);           // t3 depth
        bool debugE = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_DEBUG") == "1";
        ID3D12PipelineState pso = debugE ? combineDebugPso : combinePso;
        target.RenderColorOnly(cl =>
        {
            cl.SetGraphicsRootSignature(combineRootSig);
            cl.SetPipelineState(pso);                  // additive One/One blend (or opaque replace when debugE)
            cl.SetDescriptorHeaps(combineSrv.Heap);
            cl.SetGraphicsRootDescriptorTable(0, combineSrv.Gpu(cb));
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
        bool bounce = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_NOBOUNCE") != "1";
        *(LumenCardConstants*)cardCbMapped = new LumenCardConstants
        {
            SunDir = sunDir, SunBias = 0.03f, SunColor = ctx.LightColor, LightCount = clusteredLights.LightCount,
            InstanceCount = (uint)scene.InstanceCount, TotalTris = (uint)scene.TotalTriangles,
            SkyIntensity = skyIntensity, UseSky = useSky ? 1f : 0f, SkyVisRays = 4f,
            EmaAlpha = emaAlpha, BounceRays = bounce ? 4f : 0f,
            HistoryValid = (scene.HistoryValid && !ctx.DeterministicCapture) ? 1f : 0f,
        };

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Dx12DescriptorHeap bindless = Dx12Backend.BindlessHeap;
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(CardSkyTableBase + 0), ibl.IrradianceSrv, heapType);   // t1 sky cube

        // Ping-pong: write THIS frame's buffer (UAV), read the PREVIOUS frame's (non-pixel SRV) for the EMA +
        // 2nd bounce. Transition each to the needed state; after the dispatch bring the WRITE buffer to non-
        // pixel SRV too (the trace reads it as a root SRV). The read buffer stays SRV for next frame's swap.
        ID3D12Resource cardW = scene.CardRadianceWrite;
        ID3D12Resource cardR = scene.CardRadianceRead;
        dev.ExecuteSync(cl =>
        {
            if (scene.StateOf(cardW) != ResourceStates.UnorderedAccess)
                cl.ResourceBarrierTransition(cardW, scene.StateOf(cardW), ResourceStates.UnorderedAccess);
            if (scene.StateOf(cardR) != ResourceStates.NonPixelShaderResource)
                cl.ResourceBarrierTransition(cardR, scene.StateOf(cardR), ResourceStates.NonPixelShaderResource);
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
            cl.Dispatch((uint)((scene.TotalTriangles + 63) / 64), 1, 1);
            cl.ResourceBarrierTransition(cardW, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
        });
        scene.SetState(cardW, ResourceStates.NonPixelShaderResource);
        scene.SetState(cardR, ResourceStates.NonPixelShaderResource);
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
        var table = new RootParameter1(new RootDescriptorTable1(srvRange, uavRange), ShaderVisibility.All);
        var matSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(7, 0), ShaderVisibility.All);
        var instSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(8, 0), ShaderVisibility.All);
        var lightSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(9, 0), ShaderVisibility.All);
        var cardSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(10, 0), ShaderVisibility.All);  // t10 CardRadiance
        var metaSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(11, 0), ShaderVisibility.All);  // t11 InstanceMeta
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
                new[] { cbv0, cbv1, tlasSrv, table, matSrv, instSrv, lightSrv, cardSrv, metaSrv }, new[] { clampSamp, wrapSamp })));

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

        // --- combine root sig: 4-SRV table (E / albedo / material / depth) + clamp sampler. Additive PSO. ---
        var combRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 4, baseShaderRegister: 0);
        var combTable = new RootParameter1(new RootDescriptorTable1(combRange), ShaderVisibility.Pixel);
        var combSamp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        combineRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { combTable }, new[] { combSamp })));

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
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 8, shaderVisible: true, framesInFlight: dev.FramesInFlight);

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
                new[] { cbv0, tlasSrv, uavRoot, skyTable, instMeta, rtInst, mats, lights, prevCard }, new[] { clamp, wrap })));

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
        denoiseRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, table })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("LumenGi.hlsl");
        byte[] cs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSDenoise", "LumenGi.hlsl");
        denoisePso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription { RootSignature = denoiseRootSig, ComputeShader = cs });

        int cbSize = (Marshal.SizeOf<DenoiseConstants>() + 255) & ~255;
        denoiseCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        denoiseCbMapped = denoiseCb.Map<byte>(0);
        denoiseSrv = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 4, shaderVisible: true, framesInFlight: dev.FramesInFlight);
    }

    // The indirect buffer + the (unused-in-P2) scratch reallocate with the render resolution. Full-res for now
    // (half-res + depth-aware upsample is a P4 perf follow-up). Committed (cross-pass scratch; never pooled).
    public void Resize(int w, int h)
    {
        indirect?.Dispose();
        indirectFiltered?.Dispose();
        indirect = new Dx12OffscreenTarget(dev, Math.Max(1, w), Math.Max(1, h), withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: true);
        indirectFiltered = new Dx12OffscreenTarget(dev, Math.Max(1, w), Math.Max(1, h), withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: true);
    }

    public void Dispose()
    {
        scene.Dispose();
        tracePso?.Dispose(); traceRootSig?.Dispose(); traceCb?.Dispose(); sunCb?.Dispose();
        cardPso?.Dispose(); cardRootSig?.Dispose(); cardCb?.Dispose();
        denoisePso?.Dispose(); denoiseRootSig?.Dispose(); denoiseCb?.Dispose(); denoiseSrv?.Dispose();
        combinePso?.Dispose(); combineDebugPso?.Dispose(); combineRootSig?.Dispose(); combineSrv?.Dispose();
        indirect?.Dispose(); indirectFiltered?.Dispose();
    }
}
