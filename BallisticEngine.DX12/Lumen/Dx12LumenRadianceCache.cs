using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// Lumen FAZ 7 — WORLD-SPACE RADIANCE CACHE driver.
//
// A sparse, persistent, camera-centered clipmap of octahedral world-space radiance probes — the FAR-FIELD GI noise
// reducer + its own temporal denoiser. The screen probes (FAZ 6) trace SHORT rays and, on a miss within the cell's
// space-diagonal trace-stop, MARK the covering cell + SAMPLE this cache for the distant radiance.
//
// 1-FRAME-DEFERRED scheme (UE source-confirmed): frame N's screen probes MARK cells for next frame. THIS Build() runs
// at the START of the Lumen GI (BEFORE the screen-probe gather), allocating + tracing + fixing the cells marked LAST
// frame — so the cache is filled with whatever's currently allocated when the screen probes sample it. The screen
// probes then mark cells for NEXT frame. All cache resources persist across frames (allocated once, never per-frame).
//
// Passes (compute, this order): CSAllocate (consume last-frame marks → allocate/refresh/evict + build trace list,
// then clear the marks) → CSTrace (the budgeted probes trace FAR via LumenTrace → RadianceAtlas + HitDistAtlas) →
// CSFixup (octahedral border). All resources resolve from ResourceDescriptorHeap[] via reserved-tail bindless slots,
// so the SAME bound bindless heap serves the cache build AND the screen-probe sampling.
//
// HARD-WON RULES obeyed: persistent bindless = reserved tail (Dx12BindlessTail.LumenRadianceCacheTableBase) NOT the
// dynamic cursor; mid-frame upload via dev.DeferredRelease (the freelist init upload); NaN scrub = ternary in HLSL.
internal sealed class Dx12LumenRadianceCache : IDisposable
{
    readonly Dx12Device dev;

    // Clipmap shape (env-tunable).
    int gridRes = 32;                 // probes-grid + indirection volume resolution per axis
    // Half-extent (cm) each side of the camera → the clipmap cube is 2*extentCm on a side (25m default → 50m cube).
    // probeSpacing = 2*extent/GridRes. BALLISTIC_DX12_LUMEN_RC_EXTENT.
    float extentCm = 2500f;
    int probeRes = 16;                // octahedral probe resolution
    int finalProbeRes => probeRes + 2;   // +1 border each side
    int atlasInProbes = 64;           // probes per atlas axis → atlas = finalProbeRes*atlasInProbes square
    int atlasCapacity => atlasInProbes * atlasInProbes;
    int traceBudget = 256;            // newly-allocated/re-traced probes per frame (flat budget)
    int evictFrames = 8;

    float probeSpacing => 2f * extentCm / gridRes;   // world distance between adjacent probe grid points
    public float TraceStop => probeSpacing * 1.7320508f;   // cell space-diagonal = spacing*sqrt(3) (THE near/far split)

    // Persistent resources.
    ID3D12Resource indirection;       // GridRes^3 R32_UINT 3D texture (atlas index or 0xFFFFFFFF)
    ID3D12Resource markBuffer;        // GridRes^3 flat R32_UINT (atomic OR'd by the screen-probe trace)
    ID3D12Resource radianceAtlas;     // RGBA16F (finalProbeRes*atlasInProbes)^2
    ID3D12Resource hitDistAtlas;      // R16F same dims
    ID3D12Resource freeList;          // structured uint[] (free-stack + lastused + trace list)
    ResourceStates indirState = ResourceStates.UnorderedAccess;
    ResourceStates markState  = ResourceStates.UnorderedAccess;
    ResourceStates radState   = ResourceStates.UnorderedAccess;
    ResourceStates hitState   = ResourceStates.UnorderedAccess;
    ResourceStates freeState  = ResourceStates.UnorderedAccess;

    // Reserved-tail bindless slots (see Dx12BindlessTail.LumenRadianceCacheTableBase layout).
    const int Base = Dx12BindlessTail.LumenRadianceCacheTableBase;
    const int IndirUavSlot = Base + 0;
    const int IndirSrvSlot = Base + 1;
    const int MarkUavSlot  = Base + 2;
    const int RadUavSlot   = Base + 3;
    const int RadSrvSlot   = Base + 4;
    const int HitUavSlot   = Base + 5;
    const int HitSrvSlot   = Base + 6;
    const int FreeUavSlot  = Base + 7;

    // Public bindless indices the screen-probe sampler reads (RC_PARAMS).
    public int IndirBindless => IndirSrvSlot;
    public int RadBindless   => RadSrvSlot;
    public int HitBindless   => HitSrvSlot;
    public int MarkBindless  => MarkUavSlot;   // the screen probe atomically OR's into the mark buffer (UAV)
    public int GridRes => gridRes;
    public int ProbeResPub => probeRes;
    public int FinalProbeResPub => finalProbeRes;
    public int AtlasInProbesPub => atlasInProbes;
    public float ProbeSpacingPub => probeSpacing;
    public Vector3 Origin { get; private set; }
    public bool Valid { get; private set; }

    // FAZ 10 — the cache's three sampling textures, exposed so a consumer pass (transparent forward) that does NOT use
    // the bindless heap can create its OWN SRVs over them in its own descriptor heap (explicit t-slots). The bindless
    // indices above (IndirBindless/RadBindless/HitBindless) remain the path for HeapDirectlyIndexed consumers (fog,
    // screen probe). All three rest in UnorderedAccess (read as SRV cross-pass, the established pattern).
    public ID3D12Resource IndirectionTex => indirection;
    public ID3D12Resource RadianceTex => radianceAtlas;
    public ID3D12Resource HitDistTex => hitDistAtlas;

    // Pipeline.
    ID3D12RootSignature rootSig;
    ID3D12PipelineState initPso, allocPso, tracePso, fixupPso;
    Dx12FrameCb<RcConstants> rcCb;
    bool built;
    bool resourcesReady;
    bool initDone;
    int frameCounter;
    bool loggedBuild;

    int FreeListUintCount => 1 + 2 * atlasCapacity + 1 + 2 * traceBudget;   // top + stack + lastused + traceCount + pairs
    const uint RC_UNALLOC = 0xFFFFFFFFu;

    [StructLayout(LayoutKind.Sequential)]
    struct RcConstants
    {
        // --- LumenTrace parameter block (MUST be first; the include reads these by name) ---
        public Vector3 LtClipOrigin;   public float LtVoxelSize;
        public Vector3 LtCamPosUnused; public float LtClipHalfExtent;
        public uint LtClipResX, LtClipResY, LtClipResZ; public float LtMaxTraceDist;
        public uint LtAtlasSize, LtCardCount, LtInstanceCount, LtFinalReadIdx;
        public uint LtClipmapIdx, LtFinalValid, LtHasTlas, LtSkyIdx;
        public float LtSkyIntensity, LtUseSky, LtSurfBias, LtPad0;
        // --- radiance-cache params ---
        public Vector3 RcOrigin;       public float RcProbeSpacing;
        public uint RcGridRes;         public uint RcAtlasInProbes; public uint RcProbeRes; public uint RcFinalProbeRes;
        public float RcFarMaxDist;     public float RcPreferSW;     public uint RcFrameIndex; public uint RcTraceBudget;
        public uint RcEvictFrames;     public uint RcIndirIdx;      public uint RcRadIdx;     public uint RcHitIdx;
        public uint RcMarkIdx;         public uint RcFreeListIdx;   public float RcSkyIntensity2; public float RcUseSky2;
        public uint RcFreeCount;       public uint RcAtlasCapacity; public float RcRcPad0;    public float RcRcPad1;
    }

    public Dx12LumenRadianceCache(Dx12Device device) { dev = device; ReadEnv(); }

    static float EnvF(string n, float f) =>
        float.TryParse(Environment.GetEnvironmentVariable(n), System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : f;

    void ReadEnv()
    {
        gridRes = Math.Clamp((int)EnvF("BALLISTIC_DX12_LUMEN_RC_GRID", gridRes), 8, 64);
        extentCm = MathF.Max(100f, EnvF("BALLISTIC_DX12_LUMEN_RC_EXTENT", extentCm));
        probeRes = Math.Clamp((int)EnvF("BALLISTIC_DX12_LUMEN_RC_PROBE_RES", probeRes), 8, 32);
        atlasInProbes = Math.Clamp((int)EnvF("BALLISTIC_DX12_LUMEN_RC_ATLAS_PROBES", atlasInProbes), 8, 128);
        traceBudget = Math.Clamp((int)EnvF("BALLISTIC_DX12_LUMEN_RC_BUDGET", traceBudget), 1, atlasCapacity);
        evictFrames = Math.Clamp((int)EnvF("BALLISTIC_DX12_LUMEN_RC_EVICT", evictFrames), 1, 240);
    }

    // BUILD the cache for this frame (allocate last-frame marks → trace → fixup). Call BEFORE the screen-probe gather.
    // `cards` must be a FinalLighting-lit surface cache; `globalSdf` may be null (HW backend). Returns true if valid.
    public unsafe bool Build(Dx12FrameContext ctx, Dx12LumenCardScene cards, Dx12GlobalSdf globalSdf)
    {
        if (cards is null || !cards.Valid || cards.CardCount == 0 || !cards.FinalValid) return false;

        Dx12SceneAS sceneAS = ctx.Dxr?.SceneAS;
        bool hasTlas = sceneAS != null && sceneAS.Valid;
        bool forceSW = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_RC_SW") == "1"
                       || Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_PROBE_SW") == "1";
        bool sdfReady = globalSdf != null && globalSdf.Valid && globalSdf.ClipmapSrvBindless >= 0;
        // DETERMINISM: prefer the SW march under a deterministic capture (clipmap ready) — the HW closest-hit RayQuery
        // tie-breaks at edges run-to-run, breaking the byte-exact golden-diff. Same rationale as the screen probe.
        bool preferSW = forceSW || !hasTlas || (ctx.DeterministicCapture && sdfReady);
        if ((preferSW && !sdfReady) || (!preferSW && !hasTlas)) return false;

        EnsureBuilt();
        EnsureResources();
        globalSdf?.ToPixelShaderResource();   // SW march reads the clipmap as a (bindless) SRV.

        // Camera-centered, voxel-snapped origin (min corner) — like GlobalSdf (kills shimmer).
        float spacing = probeSpacing;
        Vector3 c = ctx.CamPos;
        Vector3 snapped = new(MathF.Floor(c.X / spacing) * spacing,
                              MathF.Floor(c.Y / spacing) * spacing,
                              MathF.Floor(c.Z / spacing) * spacing);
        Origin = snapped - new Vector3(extentCm);   // full extent each side → min corner

        float farMaxDist = EnvF("BALLISTIC_DX12_LUMEN_RC_FARDIST",
            globalSdf != null ? globalSdf.ClipWorldExtent * 1.8f : 1e4f);
        int clipIdx = globalSdf?.ClipmapSrvBindless ?? -1;
        uint frameIdx = ctx.DeterministicCapture ? (uint)Math.Min(frameCounter, 4096) : (uint)frameCounter;

        rcCb.Write(new RcConstants
        {
            LtClipOrigin = globalSdf?.ClipOrigin ?? Vector3.Zero,
            LtVoxelSize = globalSdf?.ClipVoxelSize ?? 1f,
            LtClipHalfExtent = globalSdf?.ClipHalf ?? 1f,
            LtClipResX = (uint)(globalSdf?.ClipRes ?? 1), LtClipResY = (uint)(globalSdf?.ClipRes ?? 1),
            LtClipResZ = (uint)(globalSdf?.ClipRes ?? 1), LtMaxTraceDist = farMaxDist,
            LtAtlasSize = (uint)cards.AtlasSize, LtCardCount = (uint)cards.CardCount,
            LtInstanceCount = (uint)cards.InstanceCount, LtFinalReadIdx = (uint)Math.Max(cards.FinalReadSrvIdx, 0),
            LtClipmapIdx = (uint)Math.Max(clipIdx, 0), LtFinalValid = cards.FinalValid ? 1u : 0u,
            LtHasTlas = hasTlas ? 1u : 0u, LtSkyIdx = 0u,
            LtSkyIntensity = 0f, LtUseSky = 0f, LtSurfBias = 0.03f, LtPad0 = 0f,
            RcOrigin = Origin, RcProbeSpacing = spacing,
            RcGridRes = (uint)gridRes, RcAtlasInProbes = (uint)atlasInProbes,
            RcProbeRes = (uint)probeRes, RcFinalProbeRes = (uint)finalProbeRes,
            RcFarMaxDist = farMaxDist, RcPreferSW = preferSW ? 1f : 0f,
            RcFrameIndex = frameIdx, RcTraceBudget = (uint)traceBudget,
            RcEvictFrames = (uint)evictFrames, RcIndirIdx = (uint)IndirUavSlot,
            RcRadIdx = (uint)RadUavSlot, RcHitIdx = (uint)HitUavSlot,
            RcMarkIdx = (uint)MarkUavSlot, RcFreeListIdx = (uint)FreeUavSlot,
            RcSkyIntensity2 = 0f, RcUseSky2 = 0f,
            RcFreeCount = (uint)atlasCapacity, RcAtlasCapacity = (uint)atlasCapacity,
        });

        if (!loggedBuild) { loggedBuild = true;
            Console.WriteLine($"[LumenRadianceCache] BUILD grid={gridRes}^3 extent={extentCm:0}cm spacing={spacing:0.#}cm " +
                $"traceStop={TraceStop:0.#} probeRes={probeRes} atlas={finalProbeRes*atlasInProbes}^2 cap={atlasCapacity} " +
                $"budget={traceBudget} backend={(preferSW?"SW":"HW")} farDist={farMaxDist:0.#}"); }

        ulong tlasAddr = hasTlas ? sceneAS.TlasAddress : 0;
        ulong cardAddr = cards.CardBufferGpuAddress;
        ulong pageAddr = cards.PageBufferGpuAddress;
        ulong rangeAddr = cards.RangeBufferGpuAddress != 0 ? cards.RangeBufferGpuAddress : cardAddr;
        Dx12DescriptorHeap bindless = Dx12Backend.BindlessHeap;

        // Reset the per-frame trace-count slot in the free list (CSAllocate atomically APPENDS to it, so it MUST be 0
        // before any allocate thread runs). Zeroed via a single-uint upload copy recorded into the open frame list.
        ToUav();
        uint traceCountOff = (uint)(1 + 2 * atlasCapacity);   // matches TRACE_COUNT_OFF in HLSL
        ZeroTraceCount(traceCountOff);

        int gGrid = (gridRes + 3) / 4;
        int traceCols = traceBudget * probeRes;               // CSTrace global x span
        int fixupCols = traceBudget * finalProbeRes;          // CSFixup global x span

        dev.ExecuteSync(cl =>
        {
            cl.SetDescriptorHeaps(bindless.Heap);
            cl.SetComputeRootSignature(rootSig);

            void Roots()
            {
                cl.SetComputeRootConstantBufferView(0, rcCb.Gpu);
                if (tlasAddr != 0) cl.SetComputeRootShaderResourceView(1, tlasAddr);   // t0 TLAS
                cl.SetComputeRootShaderResourceView(2, cardAddr);                       // t1 Cards
                cl.SetComputeRootShaderResourceView(3, pageAddr);                       // t2 Pages
                cl.SetComputeRootShaderResourceView(4, rangeAddr);                      // t3 ranges
            }

            // INIT (ONCE): clear indirection → RC_UNALLOC + zero the mark buffer (the free list was CPU-initialized).
            if (!initDone)
            {
                cl.SetPipelineState(initPso);
                Roots();
                cl.Dispatch((uint)gGrid, (uint)gGrid, (uint)gGrid);
                cl.ResourceBarrierUnorderedAccessView(indirection);
                cl.ResourceBarrierUnorderedAccessView(markBuffer);
            }

            // ALLOCATE (consume last-frame marks → allocate/refresh/evict + trace list; clear marks).
            cl.SetPipelineState(allocPso);
            Roots();
            cl.Dispatch((uint)gGrid, (uint)gGrid, (uint)gGrid);
            cl.ResourceBarrierUnorderedAccessView(freeList);
            cl.ResourceBarrierUnorderedAccessView(indirection);
            cl.ResourceBarrierUnorderedAccessView(markBuffer);

            // TRACE (budgeted probes → atlas).
            cl.SetPipelineState(tracePso);
            Roots();
            cl.Dispatch((uint)((traceCols + 7) / 8), (uint)((probeRes + 7) / 8), 1);
            cl.ResourceBarrierUnorderedAccessView(radianceAtlas);
            cl.ResourceBarrierUnorderedAccessView(hitDistAtlas);

            // FIXUP (octahedral border).
            cl.SetPipelineState(fixupPso);
            Roots();
            cl.Dispatch((uint)((fixupCols + 7) / 8), (uint)((finalProbeRes + 7) / 8), 1);
            cl.ResourceBarrierUnorderedAccessView(radianceAtlas);
            cl.ResourceBarrierUnorderedAccessView(hitDistAtlas);
        });

        initDone = true;

        // Promote the atlas + indirection to readable for the screen-probe sampling (NON_PIXEL: read from compute).
        ToReadable();
        Valid = true;
        frameCounter++;
        return true;
    }

    // Zero just the trace-count element each frame via a single-uint upload copy (recorded into the open frame list;
    // upload deferred-released). The whole free list otherwise persists (stack + lastused survive across frames).
    unsafe void ZeroTraceCount(uint elemIndex)
    {
        ID3D12Resource upload = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(4), ResourceStates.GenericRead);
        uint* p = upload.Map<uint>(0); p[0] = 0u; upload.Unmap(0);
        dev.ExecuteSync(cl =>
        {
            if (freeState != ResourceStates.CopyDest)
                cl.ResourceBarrierTransition(freeList, freeState, ResourceStates.CopyDest);
            cl.CopyBufferRegion(freeList, (ulong)elemIndex * 4, upload, 0, 4);
            cl.ResourceBarrierTransition(freeList, ResourceStates.CopyDest, ResourceStates.UnorderedAccess);
            freeState = ResourceStates.UnorderedAccess;
        });
        dev.DeferredRelease(upload);
    }

    void ToUav()
    {
        dev.ExecuteSync(cl =>
        {
            if (indirState != ResourceStates.UnorderedAccess) { cl.ResourceBarrierTransition(indirection, indirState, ResourceStates.UnorderedAccess); indirState = ResourceStates.UnorderedAccess; }
            if (markState  != ResourceStates.UnorderedAccess) { cl.ResourceBarrierTransition(markBuffer,  markState,  ResourceStates.UnorderedAccess); markState  = ResourceStates.UnorderedAccess; }
            if (radState   != ResourceStates.UnorderedAccess) { cl.ResourceBarrierTransition(radianceAtlas, radState, ResourceStates.UnorderedAccess); radState   = ResourceStates.UnorderedAccess; }
            if (hitState   != ResourceStates.UnorderedAccess) { cl.ResourceBarrierTransition(hitDistAtlas, hitState,  ResourceStates.UnorderedAccess); hitState   = ResourceStates.UnorderedAccess; }
        });
    }

    void ToReadable()
    {
        dev.ExecuteSync(cl =>
        {
            if (indirState != ResourceStates.NonPixelShaderResource) { cl.ResourceBarrierTransition(indirection, indirState, ResourceStates.NonPixelShaderResource); indirState = ResourceStates.NonPixelShaderResource; }
            if (radState   != ResourceStates.NonPixelShaderResource) { cl.ResourceBarrierTransition(radianceAtlas, radState, ResourceStates.NonPixelShaderResource); radState = ResourceStates.NonPixelShaderResource; }
            if (hitState   != ResourceStates.NonPixelShaderResource) { cl.ResourceBarrierTransition(hitDistAtlas, hitState,  ResourceStates.NonPixelShaderResource); hitState = ResourceStates.NonPixelShaderResource; }
            // markBuffer + freeList stay UAV (the screen probe writes the marks; freelist persists).
        });
    }

    // Count of allocated probes (debug): readback the freelist top → cap - free = allocated. Gated, runs once.
    public unsafe void DumpStats()
    {
        if (!resourcesReady) return;
        using ID3D12Resource rb = dev.Device.CreateCommittedResource(HeapProperties.ReadbackHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(8), ResourceStates.CopyDest);
        dev.ExecuteSyncImmediate(cl =>
        {
            var before = freeState;
            if (before != ResourceStates.CopySource) cl.ResourceBarrierTransition(freeList, before, ResourceStates.CopySource);
            cl.CopyBufferRegion(rb, 0, freeList, 0, 4);                                     // free top
            cl.CopyBufferRegion(rb, 4, freeList, (ulong)(1 + 2 * atlasCapacity) * 4, 4);     // trace count
            if (before != ResourceStates.CopySource) cl.ResourceBarrierTransition(freeList, ResourceStates.CopySource, before);
        });
        uint* p = rb.Map<uint>(0);
        uint freeTop = p[0], traceCnt = p[1];
        rb.Unmap(0);
        int allocated = atlasCapacity - (int)freeTop;
        string line = $"[LumenRadianceCache STATS] allocated={allocated}/{atlasCapacity} freeTop={freeTop} " +
                      $"tracedThisFrame={Math.Min((int)traceCnt, traceBudget)} occupancy={100.0 * allocated / atlasCapacity:0.#}%";
        Console.WriteLine(line);
    }

    unsafe void EnsureBuilt()
    {
        if (built) return;
        built = true;

        // CBV b0 | t0 TLAS (root SRV) | t1 Cards / t2 Pages / t3 ranges (root SRVs) | s0/s1. HeapDirectlyIndexed so the
        // cache resources (indirection/mark/atlas/freelist) + the LumenTrace include's clipmap/FinalLighting resolve
        // from ResourceDescriptorHeap[] via the reserved-tail slots / CB indices. No descriptor table needed.
        var cbv    = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var tlas   = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var cards  = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.All);
        var pages  = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(2, 0), ShaderVisibility.All);
        var ranges = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(3, 0), ShaderVisibility.All);
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
        rootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(
                RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
                new[] { cbv, tlas, cards, pages, ranges }, new[] { clamp, wrap })));

        // Prepend the LumenTrace include (no DXC include handler — established source-prepend pattern).
        string inc  = EmbeddedShaderSource.ReadHlsl("Lumen/LumenTrace.hlsl");
        string body = EmbeddedShaderSource.ReadHlsl("Lumen/LumenRadianceCache.hlsl");
        body = System.Text.RegularExpressions.Regex.Replace(
            body, "(?m)^\\s*#include\\s+\"Lumen/LumenTrace\\.hlsl\".*$", inc);

        ID3D12PipelineState Pso(string entry) => dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription
        {
            RootSignature = rootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, body, entry, "LumenRadianceCache.hlsl"),
        });
        initPso  = Pso("CSInit");
        allocPso = Pso("CSAllocate");
        tracePso = Pso("CSTrace");
        fixupPso = Pso("CSFixup");
        rcCb = new Dx12FrameCb<RcConstants>(dev);
    }

    unsafe void EnsureResources()
    {
        if (resourcesReady) return;
        resourcesReady = true;

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Dx12DescriptorHeap bindless = Dx12Backend.BindlessHeap;

        // --- indirection (GridRes^3 R32_UINT 3D) ---
        indirection = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            new ResourceDescription
            {
                Dimension = ResourceDimension.Texture3D, Width = (ulong)gridRes, Height = (uint)gridRes,
                DepthOrArraySize = (ushort)gridRes, MipLevels = 1, Format = Format.R32_UInt,
                SampleDescription = new SampleDescription(1, 0), Layout = TextureLayout.Unknown,
                Flags = ResourceFlags.AllowUnorderedAccess,
            }, ResourceStates.UnorderedAccess);
        indirection.Name = "LumenRcIndirection";
        dev.Device.CreateUnorderedAccessView(indirection, null, new UnorderedAccessViewDescription
        {
            Format = Format.R32_UInt, ViewDimension = UnorderedAccessViewDimension.Texture3D,
            Texture3D = new Texture3DUnorderedAccessView { FirstWSlice = 0, WSize = (uint)gridRes, MipSlice = 0 },
        }, bindless.Cpu(IndirUavSlot));
        dev.Device.CreateShaderResourceView(indirection, new ShaderResourceViewDescription
        {
            Format = Format.R32_UInt, ViewDimension = ShaderResourceViewDimension.Texture3D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture3D = new Texture3DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        }, bindless.Cpu(IndirSrvSlot));

        // --- mark buffer (GridRes^3 flat R32_UINT structured) ---
        int markElems = gridRes * gridRes * gridRes;
        markBuffer = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)markElems * 4, ResourceFlags.AllowUnorderedAccess),
            ResourceStates.UnorderedAccess);
        markBuffer.Name = "LumenRcMark";
        dev.Device.CreateUnorderedAccessView(markBuffer, null, new UnorderedAccessViewDescription
        {
            Format = Format.Unknown, ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = (uint)markElems, StructureByteStride = 4, CounterOffsetInBytes = 0 },
        }, bindless.Cpu(MarkUavSlot));

        // --- radiance + hit-dist atlases ---
        int atlasDim = finalProbeRes * atlasInProbes;
        radianceAtlas = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            new ResourceDescription
            {
                Dimension = ResourceDimension.Texture2D, Width = (ulong)atlasDim, Height = (uint)atlasDim,
                DepthOrArraySize = 1, MipLevels = 1, Format = Format.R16G16B16A16_Float,
                SampleDescription = new SampleDescription(1, 0), Layout = TextureLayout.Unknown,
                Flags = ResourceFlags.AllowUnorderedAccess | ResourceFlags.AllowRenderTarget,   // RTV for the one-time clear
            }, ResourceStates.UnorderedAccess);
        radianceAtlas.Name = "LumenRcRadiance";
        MakeTexViews(radianceAtlas, Format.R16G16B16A16_Float, RadUavSlot, RadSrvSlot, bindless, heapType);

        hitDistAtlas = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            new ResourceDescription
            {
                Dimension = ResourceDimension.Texture2D, Width = (ulong)atlasDim, Height = (uint)atlasDim,
                DepthOrArraySize = 1, MipLevels = 1, Format = Format.R16_Float,
                SampleDescription = new SampleDescription(1, 0), Layout = TextureLayout.Unknown,
                Flags = ResourceFlags.AllowUnorderedAccess | ResourceFlags.AllowRenderTarget,   // RTV for the one-time clear
            }, ResourceStates.UnorderedAccess);
        hitDistAtlas.Name = "LumenRcHitDist";
        MakeTexViews(hitDistAtlas, Format.R16_Float, HitUavSlot, HitSrvSlot, bindless, heapType);

        // --- free list (initialized on the CPU then uploaded: all atlas slots free, lastused 0) ---
        int n = FreeListUintCount;
        uint[] init = new uint[n];
        init[0] = (uint)atlasCapacity;                       // FREE_TOP: all slots free
        for (int i = 0; i < atlasCapacity; i++)
            init[1 + i] = (uint)i;                            // FREE_STACK: slot indices 0..cap-1
        // lastused (1+cap .. 1+2cap) left 0; trace count + pairs left 0.
        freeList = dev.CreateUavBuffer<uint>(init, ResourceStates.UnorderedAccess);
        freeList.Name = "LumenRcFreeList";
        dev.Device.CreateUnorderedAccessView(freeList, null, new UnorderedAccessViewDescription
        {
            Format = Format.Unknown, ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = (uint)n, StructureByteStride = 4, CounterOffsetInBytes = 0 },
        }, bindless.Cpu(FreeUavSlot));

        // DETERMINISM: the radiance + hit-dist atlases are CreateCommittedResource (NOT zero-filled) and are filled
        // SPARSELY — only `traceBudget` probes are traced per frame, so freshly-allocated cells whose atlas slot has
        // not been traced yet are SAMPLED by the screen probe while still holding uninitialized GPU memory → garbage
        // that differs run-to-run (the early-frame golden-diff alternates). CSInit clears indirection + mark but NOT
        // these atlases. Clear both to 0 ONCE on creation via a transient RTV clear (the card-scene ClearAtlases
        // pattern — avoids the shader-visible-heap UAV-clear rule). One-time creation cost, not per-frame.
        ClearAtlasesOnce();

        // The indirection volume + mark buffer are cleared to RC_UNALLOC / 0 by CSInit on the first Build (the free
        // list was just CPU-initialized with all slots free).
        indirState = ResourceStates.UnorderedAccess;
        markState = ResourceStates.UnorderedAccess;
        radState = ResourceStates.UnorderedAccess;
        hitState = ResourceStates.UnorderedAccess;
        freeState = ResourceStates.UnorderedAccess;
    }

    // One-time RTV clear of the radiance + hit-dist atlases to 0 (see the determinism note in EnsureResources). RTV
    // clear, NOT ClearUnorderedAccessViewFloat — the bindless UAV CPU handle lives in the SHADER-VISIBLE heap, and a
    // UAV clear requires a NON-shader-visible CPU handle (a D3D12 rule). Both atlases have AllowRenderTarget, so a
    // transient RTV clear is valid + simple. They are left in UnorderedAccess afterwards (their resting/Build state).
    void ClearAtlasesOnce()
    {
        ID3D12Resource[] all = { radianceAtlas, hitDistAtlas };
        using ID3D12DescriptorHeap rtvHeap = dev.Device.CreateDescriptorHeap(
            new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, (uint)all.Length));
        CpuDescriptorHandle rtvStart = rtvHeap.GetCPUDescriptorHandleForHeapStart();
        uint rtvInc = dev.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        for (int k = 0; k < all.Length; k++)
        {
            var h = rtvStart; h.Ptr += (nuint)(k * (int)rtvInc);
            dev.Device.CreateRenderTargetView(all[k], null, h);
        }
        // ExecuteSyncImmediate so the clear completes before the transient RTV heap disposes (one-time creation path).
        dev.ExecuteSyncImmediate(cl =>
        {
            for (int k = 0; k < all.Length; k++)
            {
                cl.ResourceBarrierTransition(all[k], ResourceStates.UnorderedAccess, ResourceStates.RenderTarget);
                var h = rtvStart; h.Ptr += (nuint)(k * (int)rtvInc);
                cl.ClearRenderTargetView(h, new Vortice.Mathematics.Color4(0, 0, 0, 0));
                cl.ResourceBarrierTransition(all[k], ResourceStates.RenderTarget, ResourceStates.UnorderedAccess);
            }
        });
    }

    void MakeTexViews(ID3D12Resource tex, Format fmt, int uavSlot, int srvSlot, Dx12DescriptorHeap bindless, DescriptorHeapType heapType)
    {
        dev.Device.CreateUnorderedAccessView(tex, null, new UnorderedAccessViewDescription
        { Format = fmt, ViewDimension = UnorderedAccessViewDimension.Texture2D }, bindless.Cpu(uavSlot));
        dev.Device.CreateShaderResourceView(tex, new ShaderResourceViewDescription
        {
            Format = fmt, ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        }, bindless.Cpu(srvSlot));
    }

    public void Dispose()
    {
        initPso?.Dispose(); allocPso?.Dispose(); tracePso?.Dispose(); fixupPso?.Dispose(); rootSig?.Dispose(); rcCb?.Dispose();
        indirection?.Dispose(); markBuffer?.Dispose(); radianceAtlas?.Dispose(); hitDistAtlas?.Dispose(); freeList?.Dispose();
    }
}
