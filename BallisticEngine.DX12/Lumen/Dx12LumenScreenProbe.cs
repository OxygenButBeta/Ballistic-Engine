using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;          // PrimitiveTopology
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// Lumen FAZ 6 — SCREEN-PROBE GATHER driver (the first VISIBLE integrated Lumen GI). Mirrors Aurora's screen-probe
// driver (Dx12AuroraGiPass.TraceScreenProbe / RecordCombine / BuildScreenProbePipeline / Resize) but swaps the
// per-direction trace for the shared LumenTrace abstraction (HW TLAS or SW global-SDF → surface-cache FinalLighting).
//
// Flow each armed frame (after LightCards lit the surface cache):
//   Place → Trace(Lumen) → Filter → SH → Integrate  → writes full-res `indirect` (incoming irradiance E)
//   Combine: add E*albedo*ao additively into ctx.SceneColor (the deferred pass already suppressed its IBL diffuse
//   ambient when ctx.LumenActiveThisFrame, so no double-count). Reuses AuroraGi.hlsl PSCombine (general E·albedo·ao).
//
// Default-off: nothing is built/allocated until the first armed frame (Ensure lazily builds PSOs + resources).
internal sealed class Dx12LumenScreenProbe : IDisposable
{
    readonly Dx12Device dev;

    // 5 probe PSOs + root sig (HeapDirectlyIndexed so the LumenTrace include's clipmap/FinalLighting bindless reads
    // resolve from ResourceDescriptorHeap[]).
    ID3D12RootSignature spRootSig;
    ID3D12PipelineState spPlacePso, spTracePso, spFilterPso, spShPso, spIntegratePso;
    Dx12FrameCb<ProbeConstants> spProbeCb;

    // Combine (additive E·albedo·ao into HDR scene color) — reuses AuroraGi.hlsl VSCombine/PSCombine.
    ID3D12RootSignature combineRootSig;
    ID3D12PipelineState combinePso, combineDebugPso;
    Dx12FrameCb<CombineConstants> combineCb;
    Dx12DescriptorHeap combineSrv;

    // Transient probe resources (committed cross-pass scratch; rebuilt on resize — NEVER pooled, the history is
    // cross-frame). Mirrors Aurora's probeHeaders/probeAtlas/probeAtlasFiltered/probeAtlasHistory/probeSH + indirect.
    Dx12OffscreenTarget indirect;            // full-res RGBA16F incoming irradiance E (the combine reads)
    ID3D12Resource probeHeaders;             // StructuredBuffer<ProbeHeader> (root UAV)
    ID3D12Resource probeHeadersPrev;         // previous frame's headers (reproject reject) — root SRV
    ID3D12Resource probeSH;                  // 7 float4 / probe (SH irradiance cache) — root UAV
    Dx12OffscreenTarget probeAtlas;          // octahedral radiance atlas, RGBA16F UAV
    Dx12OffscreenTarget probeAtlasFiltered;  // probe-space spatial-filtered atlas (integrate reads this)
    Dx12OffscreenTarget probeAtlasHistory;   // previous frame's accumulated atlas (EMA source)
    bool spHistoryValid;
    long spDescStamp = -1;

    int fullW, fullH;
    int probeStride = 24, octSize = 6;
    int probesX, probesY, probeHeaderCount;
    int frameCounter;
    bool built;
    bool loggedRun;

    const int SpTableBase = Dx12BindlessTail.LumenScreenProbeTableBase;

    [StructLayout(LayoutKind.Sequential)]
    struct ProbeConstants
    {
        // --- LumenTrace parameter block (MUST be first; the include reads these by name) ---
        public Vector3 LtClipOrigin;   public float LtVoxelSize;
        public Vector3 LtCamPosUnused; public float LtClipHalfExtent;
        public uint LtClipResX, LtClipResY, LtClipResZ; public float LtMaxTraceDist;
        public uint LtAtlasSize, LtCardCount, LtInstanceCount, LtFinalReadIdx;
        public uint LtClipmapIdx, LtFinalValid, LtHasTlas, LtSkyIdx;
        public float LtSkyIntensity, LtUseSky, LtSurfBias, LtPad0;
        // --- probe params ---
        public Matrix4x4 InvViewProj;
        public Matrix4x4 ViewProj;
        public Vector3 CameraPos;   public float Intensity;
        public Vector2 FullTexel;   public float RayCount;   public float FrameIndex;
        public float NormalBias;    public float MaxRayDist; public float PreferSW;       public float ProbeStride;
        public uint ProbesX;        public uint ProbesY;     public uint FullW;           public uint FullH;
        public float HistoryValid;  public float ProbeEma;   public float OctSize;        public float UseSH;
        public float ProbeFilterRadius; public float SpPad0; public float SpPad1;        public float SpPad2;
        // --- FAZ 7 radiance-cache params (mirror RC_PARAMS layout in LumenRadianceCacheSample.hlsl) ---
        public Vector3 RcOrigin;     public float RcProbeSpacing;
        public uint RcGridRes;       public uint RcAtlasInProbes; public uint RcProbeRes; public uint RcFinalProbeRes;
        public float RcTraceStop;    public float RcEnabled;      public uint RcIndirIdx; public uint RcRadIdx;
        public uint RcHitIdx;        public uint RcMarkIdx;       public float RcSampleBias; public float RcPad0;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CombineConstants { public float AoStrength; public Vector2 IndirectTexel; public float Pad0; }

    public Dx12LumenScreenProbe(Dx12Device device) { dev = device; }

    static float EnvF(string name, float fallback) =>
        float.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.CultureInfo.InvariantCulture,
            out float v) ? v : fallback;

    // RUN the screen-probe gather + combine. Returns true if it contributed GI (placed probes + combined).
    // `cards` must be a valid, FinalLighting-lit surface cache; `globalSdf` may be null (HW backend) but is needed
    // for the SW backend. Caller gates on Lumen armed + a valid scene.
    public unsafe bool Run(Dx12FrameContext ctx, Dx12LumenCardScene cards, Dx12GlobalSdf globalSdf,
                           Dx12LumenRadianceCache radianceCache)
    {
        if (ctx.SceneColor == null || ctx.GBuffer == null) return false;
        if (cards is null || !cards.Valid || cards.CardCount == 0 || !cards.FinalValid) return false;

        Dx12SceneAS sceneAS = ctx.Dxr?.SceneAS;
        bool hasTlas = sceneAS != null && sceneAS.Valid;
        bool forceSW = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_PROBE_SW") == "1";
        bool sdfReady = globalSdf != null && globalSdf.Valid && globalSdf.ClipmapSrvBindless >= 0;
        // DETERMINISM: the HW TLAS RayQuery resolves a CLOSEST-hit, and at triangle EDGES the committed instance/T can
        // tie-break differently run-to-run (driver BVH traversal + AS-build order) → a handful of 1-LSB pixels that
        // break the byte-exact golden-diff. The SW global-SDF march has no such tie (a deterministic sphere-march), so
        // under a deterministic capture prefer it when the clipmap is ready — the golden image stays byte-identical and
        // still shows the correct Cornell red/green bleed. Production (non-deterministic) keeps the full HW path. This
        // mirrors Aurora, which also special-cases its screen probe under DeterministicCapture for golden stability.
        bool preferSW = forceSW || !hasTlas || (ctx.DeterministicCapture && sdfReady);
        if ((preferSW && !sdfReady) || (!preferSW && !hasTlas)) {
            if (!loggedRun) { loggedRun = true;
                Console.WriteLine($"[LumenScreenProbe] SKIP no backend hasTlas={hasTlas} preferSW={preferSW} sdfReady={sdfReady}"); }
            return false;
        }

        EnsureBuilt();
        EnsureSized(ctx.SceneColor.Width, ctx.SceneColor.Height);
        globalSdf?.ToPixelShaderResource();   // SW march reads the clipmap as a (bindless) SRV.

        var gbuffer = ctx.GBuffer;
        var target = ctx.SceneColor;

        // The G-buffer depth/normal are read from a COMPUTE (non-pixel) stage; the trace samples the LIT surface
        // cache (FinalLighting, which LightCards left non-pixel SRV) via bindless. Promote G-buffer to combined read.
        gbuffer.ToShaderResource();

        Matrix4x4.Invert(ctx.ViewProj, out Matrix4x4 invVP);
        // env OVERRIDES the LumenVolume intensity (env wins for A/B; else the artist's volume value).
        float intensity = EnvF("BALLISTIC_DX12_LUMEN_INTENSITY", ctx.PostFX?.LumenIntensity ?? 1f);
        float maxDist = EnvF("BALLISTIC_DX12_LUMEN_PROBE_MAXDIST",
            globalSdf != null ? globalSdf.ClipWorldExtent * 1.8f : 1e4f);
        int clipIdx = globalSdf?.ClipmapSrvBindless ?? -1;
        bool useSH = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_PROBE_NOSH") != "1";

        // FAZ 7 radiance cache: enabled when a valid, built cache is supplied (the GiPass gates it on the RC door).
        bool rcOn = radianceCache != null && radianceCache.Valid;

        spProbeCb.Write(new ProbeConstants
        {
            LtClipOrigin = globalSdf?.ClipOrigin ?? Vector3.Zero,
            LtVoxelSize = globalSdf?.ClipVoxelSize ?? 1f,
            LtClipHalfExtent = globalSdf?.ClipHalf ?? 1f,
            LtClipResX = (uint)(globalSdf?.ClipRes ?? 1), LtClipResY = (uint)(globalSdf?.ClipRes ?? 1),
            LtClipResZ = (uint)(globalSdf?.ClipRes ?? 1), LtMaxTraceDist = maxDist,
            LtAtlasSize = (uint)cards.AtlasSize, LtCardCount = (uint)cards.CardCount,
            LtInstanceCount = (uint)cards.InstanceCount, LtFinalReadIdx = (uint)Math.Max(cards.FinalReadSrvIdx, 0),
            LtClipmapIdx = (uint)Math.Max(clipIdx, 0), LtFinalValid = cards.FinalValid ? 1u : 0u,
            LtHasTlas = hasTlas ? 1u : 0u, LtSkyIdx = 0u,
            LtSkyIntensity = 0f, LtUseSky = 0f, LtSurfBias = 0.03f, LtPad0 = 0f,
            InvViewProj = Matrix4x4.Transpose(invVP),
            ViewProj = Matrix4x4.Transpose(ctx.ViewProj),
            CameraPos = ctx.CamPos, Intensity = intensity,
            FullTexel = new Vector2(1f / indirect.Width, 1f / indirect.Height),
            RayCount = octSize * octSize, FrameIndex = ctx.DeterministicCapture ? 0f : frameCounter,
            NormalBias = 0.03f, MaxRayDist = maxDist, PreferSW = preferSW ? 1f : 0f, ProbeStride = probeStride,
            ProbesX = (uint)probesX, ProbesY = (uint)probesY,
            FullW = (uint)indirect.Width, FullH = (uint)indirect.Height,
            HistoryValid = spHistoryValid ? 1f : 0f,
            ProbeEma = EnvF("BALLISTIC_DX12_LUMEN_PROBE_EMA", 0.1f),
            OctSize = octSize, UseSH = useSH ? 1f : 0f,
            ProbeFilterRadius = EnvF("BALLISTIC_DX12_LUMEN_PROBE_FILTER_RADIUS", 2f),
            // --- FAZ 7 radiance cache ---
            RcOrigin = rcOn ? radianceCache.Origin : Vector3.Zero,
            RcProbeSpacing = rcOn ? radianceCache.ProbeSpacingPub : 1f,
            RcGridRes = rcOn ? (uint)radianceCache.GridRes : 1u,
            RcAtlasInProbes = rcOn ? (uint)radianceCache.AtlasInProbesPub : 1u,
            RcProbeRes = rcOn ? (uint)radianceCache.ProbeResPub : 1u,
            RcFinalProbeRes = rcOn ? (uint)radianceCache.FinalProbeResPub : 1u,
            RcTraceStop = rcOn ? radianceCache.TraceStop : 0f,
            RcEnabled = rcOn ? 1f : 0f,
            RcIndirIdx = rcOn ? (uint)radianceCache.IndirBindless : 0u,
            RcRadIdx = rcOn ? (uint)radianceCache.RadBindless : 0u,
            RcHitIdx = rcOn ? (uint)radianceCache.HitBindless : 0u,
            RcMarkIdx = rcOn ? (uint)radianceCache.MarkBindless : 0u,
            RcSampleBias = 0f, RcPad0 = 0f,
        });

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Dx12DescriptorHeap bindless = Dx12Backend.BindlessHeap;
        // Persistent reserved-tail table: t1 depth / t2 normal SRV; u1 atlas / u2 indirect / u3 filtered UAV;
        // t13 atlas-history SRV. Re-stamped only when a source handle changes (resize / scene swap).
        long descStamp = (long)gbuffer.DepthSrvCpu.Ptr ^ ((long)gbuffer.ColorSrvCpu(1).Ptr << 1)
            ^ ((long)probeAtlas.ColorSrvCpu.Ptr << 2) ^ ((long)indirect.ColorSrvCpu.Ptr << 3)
            ^ ((long)probeAtlasFiltered.ColorSrvCpu.Ptr << 4) ^ ((long)probeAtlasHistory.ColorSrvCpu.Ptr << 5);
        if (descStamp != spDescStamp)
        {
            spDescStamp = descStamp;
            dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(SpTableBase + 0), gbuffer.DepthSrvCpu, heapType);     // t1 depth
            dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(SpTableBase + 1), gbuffer.ColorSrvCpu(1), heapType);  // t2 normal
            dev.Device.CreateUnorderedAccessView(probeAtlas.RenderTarget, null, new UnorderedAccessViewDescription
            { Format = Dx12OffscreenTarget.HdrFormat, ViewDimension = UnorderedAccessViewDimension.Texture2D },
                bindless.Cpu(SpTableBase + 2));   // u1 probe atlas
            dev.Device.CreateUnorderedAccessView(indirect.RenderTarget, null, new UnorderedAccessViewDescription
            { Format = Dx12OffscreenTarget.HdrFormat, ViewDimension = UnorderedAccessViewDimension.Texture2D },
                bindless.Cpu(SpTableBase + 3));   // u2 indirect
            dev.Device.CreateUnorderedAccessView(probeAtlasFiltered.RenderTarget, null, new UnorderedAccessViewDescription
            { Format = Dx12OffscreenTarget.HdrFormat, ViewDimension = UnorderedAccessViewDimension.Texture2D },
                bindless.Cpu(SpTableBase + 4));   // u3 filtered
            dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(SpTableBase + 5), probeAtlasHistory.ColorSrvCpu, heapType); // t13 history
        }

        if (!loggedRun) { loggedRun = true;
            Console.WriteLine($"[LumenScreenProbe] RUN backend={(preferSW?"SW":"HW")} probes={probesX}x{probesY} oct={octSize} " +
                $"stride={probeStride} cards={cards.CardCount} inst={cards.InstanceCount} finalReadIdx={cards.FinalReadSrvIdx} " +
                $"clipIdx={clipIdx} sh={useSH} maxDist={maxDist:0.#} rc={(rcOn?$"ON traceStop={radianceCache.TraceStop:0.#}":"OFF")}"); }

        indirect.ColorToUnorderedAccess();
        probeAtlas.ColorToUnorderedAccess();
        probeAtlasFiltered.ColorToUnorderedAccess();

        ulong tlasAddr = hasTlas ? sceneAS.TlasAddress : 0;
        ulong cardAddr = cards.CardBufferGpuAddress;
        ulong pageAddr = cards.PageBufferGpuAddress;
        ulong rangeAddr = cards.RangeBufferGpuAddress != 0 ? cards.RangeBufferGpuAddress : cardAddr;
        var slotTable = bindless.Gpu(SpTableBase);   // the persistent t1-t2/u1-u3/t13 table (slot 3 below)

        void SetCommonRoots(ID3D12GraphicsCommandList cl)
        {
            cl.SetComputeRootConstantBufferView(0, spProbeCb.Gpu);
            if (tlasAddr != 0) cl.SetComputeRootShaderResourceView(1, tlasAddr);   // t0 TLAS (root SRV)
            cl.SetComputeRootShaderResourceView(2, cardAddr);                       // t1 Cards
            cl.SetComputeRootShaderResourceView(3, pageAddr);                       // t2 Pages
            cl.SetComputeRootShaderResourceView(4, rangeAddr);                      // t3 InstanceRanges
            cl.SetComputeRootDescriptorTable(5, slotTable);                         // t4 depth/t5 normal + u1/u2/u3 + t13
            cl.SetComputeRootUnorderedAccessView(6, probeHeaders.GPUVirtualAddress);     // u0 ProbeHeaders
            cl.SetComputeRootShaderResourceView(7, probeHeadersPrev.GPUVirtualAddress);  // t16 prev headers
            cl.SetComputeRootUnorderedAccessView(8, probeSH.GPUVirtualAddress);          // u4 ProbeSH
        }

        // PLACE → barrier → TRACE → snapshot history → FILTER → SH → INTEGRATE. RAW UAV chain in ONE open frame
        // list (no submit between) → explicit UAV barriers (P0a). Atlas history read from a COMPUTE shader → NON_PIXEL.
        probeAtlasHistory.ColorToNonPixelShaderResource();
        dev.ExecuteSync(cl =>
        {
            cl.SetDescriptorHeaps(bindless.Heap);
            cl.SetComputeRootSignature(spRootSig);

            // PLACE
            cl.SetPipelineState(spPlacePso);
            SetCommonRoots(cl);
            cl.Dispatch((uint)((probesX + 7) / 8), (uint)((probesY + 7) / 8), 1);
            cl.ResourceBarrierUnorderedAccessView(probeHeaders);

            // TRACE (Lumen)
            cl.SetPipelineState(spTracePso);
            SetCommonRoots(cl);
            cl.Dispatch((uint)((probesX * octSize + 7) / 8), (uint)((probesY * octSize + 7) / 8), 1);
            cl.ResourceBarrierUnorderedAccessView(probeAtlas.RenderTarget);
        });

        // Snapshot this frame's accumulated atlas + headers → history for next frame's EMA + reproject.
        probeAtlasHistory.CopyColorFrom(probeAtlas);
        dev.ExecuteSync(cl =>
        {
            cl.ResourceBarrierTransition(probeHeaders, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
            cl.ResourceBarrierTransition(probeHeadersPrev, ResourceStates.NonPixelShaderResource, ResourceStates.CopyDest);
            cl.CopyResource(probeHeadersPrev, probeHeaders);
            cl.ResourceBarrierTransition(probeHeaders, ResourceStates.CopySource, ResourceStates.UnorderedAccess);
            cl.ResourceBarrierTransition(probeHeadersPrev, ResourceStates.CopyDest, ResourceStates.NonPixelShaderResource);
        });
        spHistoryValid = true;

        probeAtlas.ColorToUnorderedAccess();
        probeAtlasFiltered.ColorToUnorderedAccess();
        bool probeFilter = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_PROBE_NOFILTER") != "1";
        dev.ExecuteSync(cl =>
        {
            cl.SetDescriptorHeaps(bindless.Heap);
            cl.SetComputeRootSignature(spRootSig);

            // FILTER
            if (probeFilter)
            {
                cl.SetPipelineState(spFilterPso);
                SetCommonRoots(cl);
                cl.Dispatch((uint)((probesX * octSize + 7) / 8), (uint)((probesY * octSize + 7) / 8), 1);
                cl.ResourceBarrierUnorderedAccessView(probeAtlasFiltered.RenderTarget);
            }

            // SH
            if (useSH)
            {
                cl.SetPipelineState(spShPso);
                SetCommonRoots(cl);
                cl.Dispatch((uint)((probesX * probesY + 63) / 64), 1, 1);
                cl.ResourceBarrierUnorderedAccessView(probeSH);
            }

            // INTEGRATE → full-res E
            cl.SetPipelineState(spIntegratePso);
            SetCommonRoots(cl);
            cl.Dispatch((uint)((indirect.Width + 7) / 8), (uint)((indirect.Height + 7) / 8), 1);
        });

        // When the filter is bypassed, the integrate reads ProbeAtlasFiltered — copy raw atlas into it.
        if (!probeFilter)
        {
            probeAtlasFiltered.CopyColorFrom(probeAtlas);
            probeAtlasFiltered.ColorToUnorderedAccess();
        }

        // COMBINE: add E·albedo·ao into the HDR scene color (deferred IBL diffuse already suppressed).
        RecordCombine(ctx, gbuffer, target);
        frameCounter++;
        return true;
    }

    // Add E·albedo·matAo (+ optional GTAO) additively into the HDR scene color via AuroraGi.hlsl PSCombine.
    unsafe void RecordCombine(Dx12FrameContext ctx, Dx12GBuffer gbuffer, Dx12OffscreenTarget target)
    {
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        gbuffer.ToShaderResource();
        indirect.ColorToShaderResource();

        bool aoOn = ctx.Doors.Ssao && ctx.PostFX.SSAOEnabled;
        float aoStrength = aoOn ? EnvF("BALLISTIC_DX12_LUMEN_AO", 0f) : 0f;
        combineCb.Write(new CombineConstants
        {
            AoStrength = aoStrength,
            IndirectTexel = new Vector2(1f / indirect.Width, 1f / indirect.Height),
        });
        combineSrv.Reset();
        int cb = combineSrv.AllocateRange(5);
        dev.Device.CopyDescriptorsSimple(1, combineSrv.Cpu(cb + 0), indirect.ColorSrvCpu, heapType);          // t0 E
        dev.Device.CopyDescriptorsSimple(1, combineSrv.Cpu(cb + 1), gbuffer.ColorSrvCpu(0), heapType);        // t1 albedo
        dev.Device.CopyDescriptorsSimple(1, combineSrv.Cpu(cb + 2), gbuffer.ColorSrvCpu(2), heapType);        // t2 material (baked ao)
        dev.Device.CopyDescriptorsSimple(1, combineSrv.Cpu(cb + 3), gbuffer.DepthSrvCpu, heapType);           // t3 depth
        dev.Device.CopyDescriptorsSimple(1, combineSrv.Cpu(cb + 4), aoOn ? ctx.AoResult : gbuffer.DepthSrvCpu, heapType); // t4 GTAO
        bool debugE = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_DEBUG") == "1";
        ID3D12PipelineState pso = debugE ? combineDebugPso : combinePso;
        target.RenderColorOnly(cl =>
        {
            cl.SetGraphicsRootSignature(combineRootSig);
            cl.SetPipelineState(pso);
            cl.SetDescriptorHeaps(combineSrv.Heap);
            cl.SetGraphicsRootConstantBufferView(0, combineCb.Gpu);
            cl.SetGraphicsRootDescriptorTable(1, combineSrv.Gpu(cb));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
    }

    // ---- build (lazy on first armed frame) ----
    unsafe void EnsureBuilt()
    {
        if (built) return;
        built = true;
        BuildProbePipeline();
        BuildCombinePipeline();
    }

    unsafe void BuildProbePipeline()
    {
        // CBV b0 | t0 TLAS (root SRV) | t1 Cards / t2 Pages / t3 InstanceRanges (root SRVs) | table{t4 depth, t5
        // normal SRV + u1 atlas, u2 indirect, u3 filtered UAV + t13 atlas-history SRV} | u0 ProbeHeaders (root UAV) |
        // t16 prev headers (root SRV) | u4 ProbeSH (root UAV) | s0/s1. HeapDirectlyIndexed so the LumenTrace include
        // resolves clipmap + FinalLighting from ResourceDescriptorHeap[].
        const DescriptorRangeFlags Vol = DescriptorRangeFlags.DataVolatile;
        var cbv0   = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var tlas   = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All);   // t0 TLAS
        var cards  = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.All);   // t1
        var pages  = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(2, 0), ShaderVisibility.All);   // t2
        var ranges = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(3, 0), ShaderVisibility.All);   // t3
        // table: t4 depth + t5 normal (2 SRV, offset 0) | u1 atlas (offset 2) | u2 indirect (offset 3) |
        //        u3 filtered (offset 4) | t13 atlas-history (offset 5).
        var gbRange    = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 2, baseShaderRegister: 4,
            registerSpace: 0, offsetInDescriptorsFromTableStart: 0, flags: Vol);   // t4,t5
        var atlasUav   = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 1,
            registerSpace: 0, offsetInDescriptorsFromTableStart: 2, flags: Vol);   // u1
        var indUav     = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 2,
            registerSpace: 0, offsetInDescriptorsFromTableStart: 3, flags: Vol);   // u2
        var filtUav    = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 3,
            registerSpace: 0, offsetInDescriptorsFromTableStart: 4, flags: Vol);   // u3
        var histRange  = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 13,
            registerSpace: 0, offsetInDescriptorsFromTableStart: 5, flags: Vol);   // t13
        var table = new RootParameter1(new RootDescriptorTable1(gbRange, atlasUav, indUav, filtUav, histRange), ShaderVisibility.All);
        var headerUav  = new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(0, 0), ShaderVisibility.All);   // u0 ProbeHeaders
        var prevHeader = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(16, 0), ShaderVisibility.All);  // t16 prev headers
        var probeShUav = new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(4, 0), ShaderVisibility.All);  // u4 ProbeSH
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
        // Roots: 0 cbv0, 1 t0 TLAS, 2 t1 Cards, 3 t2 Pages, 4 t3 Ranges, 5 table, 6 u0 headers, 7 t16 prev, 8 u4 SH.
        spRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(
                RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
                new[] { cbv0, tlas, cards, pages, ranges, table, headerUav, prevHeader, probeShUav },
                new[] { clamp, wrap })));

        // The probe shader #includes "Lumen/LumenTrace.hlsl" + "Lumen/LumenRadianceCacheSample.hlsl"; there is NO DXC
        // include handler (shaders are embedded strings) → source-prepend each include in place (strip the #include
        // line, paste the source). The established pattern (mirroring the FAZ 5 trace debug shader).
        string inc  = EmbeddedShaderSource.ReadHlsl("Lumen/LumenTrace.hlsl");
        string rcInc = EmbeddedShaderSource.ReadHlsl("Lumen/LumenRadianceCacheSample.hlsl");
        string body = EmbeddedShaderSource.ReadHlsl("Lumen/LumenScreenProbe.hlsl");
        body = System.Text.RegularExpressions.Regex.Replace(
            body, "(?m)^\\s*#include\\s+\"Lumen/LumenTrace\\.hlsl\".*$", inc);
        body = System.Text.RegularExpressions.Regex.Replace(
            body, "(?m)^\\s*#include\\s+\"Lumen/LumenRadianceCacheSample\\.hlsl\".*$", rcInc);

        ID3D12PipelineState Pso(string entry) => dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = spRootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, body, entry, "LumenScreenProbe.hlsl"),
        });
        spPlacePso     = Pso("CSPlace");
        spTracePso     = Pso("CSProbeTrace");
        spFilterPso    = Pso("CSProbeFilter");
        spShPso        = Pso("CSProbeSH");
        spIntegratePso = Pso("CSIntegrate");
        spProbeCb = new Dx12FrameCb<ProbeConstants>(dev);
    }

    unsafe void BuildCombinePipeline()
    {
        var combCbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.Pixel);
        var combRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 5, baseShaderRegister: 0);
        var combTable = new RootParameter1(new RootDescriptorTable1(combRange), ShaderVisibility.Pixel);
        var combSamp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1, ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        combineRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { combCbv, combTable }, new[] { combSamp })));

        string hlsl = EmbeddedShaderSource.ReadHlsl("AuroraGi.hlsl");   // reuse the general E·albedo·ao combine
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSCombine", "AuroraGi.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSCombine", "AuroraGi.hlsl");
        byte[] psDebug = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSDebugE", "AuroraGi.hlsl");
        var additive = new BlendDescription(Blend.One, Blend.One);
        GraphicsPipelineStateDescription Make(byte[] pixel, BlendDescription blend) => new()
        {
            RootSignature = combineRootSig, VertexShader = vs, PixelShader = pixel, InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = blend,
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat },
            DepthStencilFormat = Format.Unknown, SampleDescription = new SampleDescription(1, 0),
        };
        combinePso = dev.Device.CreateGraphicsPipelineState(Make(ps, additive));
        combineDebugPso = dev.Device.CreateGraphicsPipelineState(Make(psDebug, BlendDescription.Opaque));
        combineCb = new Dx12FrameCb<CombineConstants>(dev);
        combineSrv = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 10, shaderVisible: true,
            framesInFlight: dev.FramesInFlight);
    }

    // (Re)allocate the transient probe resources when the GI resolution changes.
    void EnsureSized(int w, int h)
    {
        int lw = Math.Max(1, w), lh = Math.Max(1, h);
        if (indirect != null && indirect.Width == lw && indirect.Height == lh) return;
        fullW = lw; fullH = lh;

        probeStride = Math.Clamp((int)EnvF("BALLISTIC_DX12_LUMEN_PROBE_STRIDE", 24f), 4, 64);
        octSize = Math.Clamp((int)EnvF("BALLISTIC_DX12_LUMEN_PROBE_OCT", 6f), 4, 16);
        probesX = (lw + probeStride - 1) / probeStride;
        probesY = (lh + probeStride - 1) / probeStride;
        probeHeaderCount = Math.Max(probesX * probesY, 1);

        indirect?.Dispose();
        indirect = new Dx12OffscreenTarget(dev, lw, lh, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: true);

        probeHeaders?.Dispose();
        probeHeaders = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(probeHeaderCount * 32), ResourceFlags.AllowUnorderedAccess),
            ResourceStates.UnorderedAccess);
        probeHeadersPrev?.Dispose();
        probeHeadersPrev = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(probeHeaderCount * 32)), ResourceStates.NonPixelShaderResource);
        probeSH?.Dispose();
        probeSH = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(probeHeaderCount * 7 * 16), ResourceFlags.AllowUnorderedAccess),
            ResourceStates.UnorderedAccess);

        int ax = Math.Max(probesX * octSize, 1), ay = Math.Max(probesY * octSize, 1);
        probeAtlas?.Dispose();
        probeAtlas = new Dx12OffscreenTarget(dev, ax, ay, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: true);
        probeAtlasFiltered?.Dispose();
        probeAtlasFiltered = new Dx12OffscreenTarget(dev, ax, ay, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: true);
        probeAtlasHistory?.Dispose();
        probeAtlasHistory = new Dx12OffscreenTarget(dev, ax, ay, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: true);
        spHistoryValid = false;
        spDescStamp = -1;
    }

    public void Dispose()
    {
        spPlacePso?.Dispose(); spTracePso?.Dispose(); spFilterPso?.Dispose(); spShPso?.Dispose(); spIntegratePso?.Dispose();
        spRootSig?.Dispose(); spProbeCb?.Dispose();
        combinePso?.Dispose(); combineDebugPso?.Dispose(); combineRootSig?.Dispose(); combineCb?.Dispose(); combineSrv?.Dispose();
        indirect?.Dispose(); probeAtlas?.Dispose(); probeAtlasFiltered?.Dispose(); probeAtlasHistory?.Dispose();
        probeHeaders?.Dispose(); probeHeadersPrev?.Dispose(); probeSH?.Dispose();
    }
}
