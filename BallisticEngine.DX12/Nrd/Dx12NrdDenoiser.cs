using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// NRD (ReBLUR_DIFFUSE) temporal denoiser, driven directly over Vortice D3D12 (no NRI layer). NRD is API-agnostic:
// CreateInstance gives us N compute pipelines (DXIL embedded in NRD.dll) + a transient/permanent texture pool +
// a shared root-sig layout; each frame GetComputeDispatches returns a dispatch list (pipelineIndex + resources +
// constants + grid) that we execute on our own command list. This file owns: instance, root signature, the PSOs,
// the pool textures, a CPU-visible descriptor heap, and a constant-buffer ring. FAZ 4.2 = init; 4.3 = dispatch.
//
// Layout (from NRDIntegration.hpp): root CBV b<cbReg> + 2 root samplers (NEAREST/LINEAR clamp) in
// space<cbAndSamplers> + one descriptor table {SRV range t<base>.., UAV range u<base>..} in space<resources>.
// We build it raw; the NRD shaders were compiled expecting exactly these registers/spaces.
internal sealed unsafe class Dx12NrdDenoiser : IDisposable {
    readonly Dx12Device dev;
    public bool Available { get; private set; }
    public uint Identifier => 1;   // our REBLUR_DIFFUSE denoiser id

    IntPtr instance;
    ID3D12RootSignature rootSig;
    ID3D12PipelineState[] pipelines;       // one per NRD pipeline
    PipelineLayout[] pipelineLayouts;      // per-pipeline resource-range layout (cached from PipelineDesc)
    PoolTex[] pool;                        // permanent pool first, then transient
    int permanentPoolSize;
    NrdApi.InstanceDesc instDesc;

    // A pipeline's resource ranges: how many TEXTURE (SRV) then STORAGE_TEXTURE (UAV) descriptors it binds, in order.
    struct PipelineLayout { public NrdApi.ResourceRangeDesc[] Ranges; }

    // GPU-visible descriptor heap for NRD's per-dispatch resource tables (SRV+UAV), bump-allocated per frame.
    ID3D12DescriptorHeap srvUavHeap;
    int srvUavHeapCapacity, srvUavHeapCursor, srvUavInc;

    // Constant-buffer ring (NRD streams one CB per dispatch).
    ID3D12Resource cb;
    byte* cbMapped;
    int cbViewSize, cbRingSize, cbCursor;

    struct PoolTex {
        public ID3D12Resource Resource;
        public Format Format;
        public ResourceStates State;
        public int Width, Height;
    }

    public Dx12NrdDenoiser(Dx12Device device) { dev = device; }

    // Build the NRD instance + all GPU resources at the given full-res. Returns false (and self-disables) if NRD
    // is unavailable (DLL missing / create failed) — the caller then keeps Aurora's own temporal accumulator.
    public bool Initialize(int width, int height) {
        try { return InitInternal(width, height); }
        catch (Exception e) { Console.WriteLine($"[NRD] init failed → Aurora temporal fallback: {e.Message}"); Dispose(); return false; }
    }

    bool InitInternal(int width, int height) {
        var dd = new NrdApi.DenoiserDesc { Identifier = Identifier, Denoiser = NrdApi.Denoiser.REBLUR_DIFFUSE };
        var icd = new NrdApi.InstanceCreationDesc { AllocationCallbacks = default, Denoisers = (IntPtr)(&dd), DenoisersNum = 1 };
        if (NrdApi.CreateInstance(in icd, out instance) != NrdApi.Result.Success || instance == IntPtr.Zero) {
            Console.WriteLine("[NRD] CreateInstance failed");
            return false;
        }
        instDesc = Marshal.PtrToStructure<NrdApi.InstanceDesc>(NrdApi.GetInstanceDesc(instance));

        BuildRootSignature();
        BuildPipelines();
        BuildPool(width, height);

        // Descriptor heap: worst case = all dispatches in a frame × max resources each. NRD's REBLUR has ~14
        // dispatches; give generous headroom (per-set max × pipelines × 2). Reset (bump) each frame.
        srvUavHeapCapacity = (int)((instDesc.DescriptorPoolDesc.PerSetTexturesMaxNum +
                                    instDesc.DescriptorPoolDesc.PerSetStorageTexturesMaxNum) * instDesc.PipelinesNum * 2 + 64);
        srvUavHeap = dev.Device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, (uint)srvUavHeapCapacity,
            DescriptorHeapFlags.ShaderVisible));
        srvUavInc = (int)dev.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

        // CB ring: each view is the denoiser's max CB size (256-aligned); ring holds many dispatches × frames.
        cbViewSize = ((int)instDesc.ConstantBufferMaxDataSize + 255) & ~255;
        cbRingSize = cbViewSize * 256;   // plenty for ~14 dispatches/frame × several frames in flight
        cb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbRingSize), ResourceStates.GenericRead);
        cbMapped = cb.Map<byte>(0);

        Available = true;
        Console.WriteLine($"[NRD] ReBLUR_DIFFUSE ready: {pipelines.Length} PSOs, {pool.Length} pool textures, " +
                          $"descHeap={srvUavHeapCapacity}, cbView={cbViewSize}B @ {width}x{height}");
        return true;
    }

    void BuildRootSignature() {
        uint cbReg = instDesc.ConstantBufferRegisterIndex;
        uint cbSpace = instDesc.ConstantBufferAndSamplersSpaceIndex;
        uint resSpace = instDesc.ResourcesSpaceIndex;
        uint sampBase = instDesc.SamplersBaseRegisterIndex;
        uint resBase = instDesc.ResourcesBaseRegisterIndex;
        uint srvCount = instDesc.DescriptorPoolDesc.PerSetTexturesMaxNum;
        uint uavCount = instDesc.DescriptorPoolDesc.PerSetStorageTexturesMaxNum;

        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(cbReg, cbSpace), ShaderVisibility.All);
        // SRVs occupy table slots [0, srvCount); UAVs append at [srvCount, ...). Both at the same base register but
        // distinct register types (t vs u), in the resources space.
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, srvCount,
            baseShaderRegister: resBase, registerSpace: resSpace, offsetInDescriptorsFromTableStart: 0);
        var uavRange = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, uavCount,
            baseShaderRegister: resBase, registerSpace: resSpace, offsetInDescriptorsFromTableStart: srvCount);
        var table = new RootParameter1(new RootDescriptorTable1(srvRange, uavRange), ShaderVisibility.All);

        // NRD's two samplers: index 0/1 map to NEAREST_CLAMP / LINEAR_CLAMP, both clamp address mode.
        var samplers = new StaticSamplerDescription[instDesc.SamplersNum];
        var samplerPtr = (NrdApi.Sampler*)instDesc.Samplers;
        for (uint i = 0; i < instDesc.SamplersNum; i++) {
            bool nearest = samplerPtr[i] == NrdApi.Sampler.NEAREST_CLAMP;
            samplers[i] = new StaticSamplerDescription(ShaderVisibility.All, sampBase + i, cbSpace) {
                Filter = nearest ? Filter.MinMagMipPoint : Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp,
                MaxAnisotropy = 1, ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
            };
        }

        rootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, table }, samplers)));
    }

    void BuildPipelines() {
        var pipePtr = (NrdApi.PipelineDesc*)instDesc.Pipelines;
        pipelines = new ID3D12PipelineState[instDesc.PipelinesNum];
        pipelineLayouts = new PipelineLayout[instDesc.PipelinesNum];
        for (uint i = 0; i < instDesc.PipelinesNum; i++) {
            NrdApi.PipelineDesc pd = pipePtr[i];
            var dxil = pd.ComputeShaderDXIL;   // bytecode + size; embedded in NRD.dll
            if (dxil.Bytecode == IntPtr.Zero || dxil.Size == 0)
                throw new InvalidOperationException($"NRD pipeline {i} has no DXIL bytecode (build NRD with NRD_EMBEDS_DXIL_SHADERS=ON)");
            var bytes = new byte[dxil.Size];
            Marshal.Copy(dxil.Bytecode, bytes, 0, (int)dxil.Size);
            pipelines[i] = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription {
                RootSignature = rootSig, ComputeShader = bytes,
            });
            // Cache the resource ranges (the dispatch's resources[] are concatenated in this order).
            var ranges = new NrdApi.ResourceRangeDesc[pd.ResourceRangesNum];
            var rangePtr = (NrdApi.ResourceRangeDesc*)pd.ResourceRanges;
            for (uint j = 0; j < pd.ResourceRangesNum; j++) ranges[j] = rangePtr[j];
            pipelineLayouts[i] = new PipelineLayout { Ranges = ranges };
        }
    }

    void BuildPool(int width, int height) {
        permanentPoolSize = (int)instDesc.PermanentPoolSize;
        int n = (int)(instDesc.PermanentPoolSize + instDesc.TransientPoolSize);
        pool = new PoolTex[n];
        var permPtr = (NrdApi.TextureDesc*)instDesc.PermanentPool;
        var tranPtr = (NrdApi.TextureDesc*)instDesc.TransientPool;
        for (int i = 0; i < n; i++) {
            NrdApi.TextureDesc td = i < permanentPoolSize ? permPtr[i] : tranPtr[i - permanentPoolSize];
            int w = Math.Max(1, width / Math.Max((int)td.DownsampleFactor, 1));
            int h = Math.Max(1, height / Math.Max((int)td.DownsampleFactor, 1));
            Format fmt = ToDxgi(td.Format);
            var rd = ResourceDescription.Texture2D(fmt, (uint)w, (uint)h, 1, 1);
            rd.Flags = ResourceFlags.AllowUnorderedAccess;
            var res = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
                rd, ResourceStates.UnorderedAccess);
            pool[i] = new PoolTex { Resource = res, Format = fmt, State = ResourceStates.UnorderedAccess, Width = w, Height = h };
        }
    }

    // NRD Format → DXGI. Only the formats NRD's REBLUR pool actually uses are mapped; extend if a new one appears.
    static Format ToDxgi(NrdApi.Format f) => f switch {
        NrdApi.Format.R8_UNORM => Format.R8_UNorm,
        NrdApi.Format.R8_SNORM => Format.R8_SNorm,
        NrdApi.Format.R8_UINT => Format.R8_UInt,
        NrdApi.Format.R8_SINT => Format.R8_SInt,
        NrdApi.Format.RG8_UNORM => Format.R8G8_UNorm,
        NrdApi.Format.RG8_SNORM => Format.R8G8_SNorm,
        NrdApi.Format.RG8_UINT => Format.R8G8_UInt,
        NrdApi.Format.RG8_SINT => Format.R8G8_SInt,
        NrdApi.Format.RGBA8_UNORM => Format.R8G8B8A8_UNorm,
        NrdApi.Format.RGBA8_SNORM => Format.R8G8B8A8_SNorm,
        NrdApi.Format.RGBA8_UINT => Format.R8G8B8A8_UInt,
        NrdApi.Format.RGBA8_SINT => Format.R8G8B8A8_SInt,
        NrdApi.Format.RGBA8_SRGB => Format.R8G8B8A8_UNorm_SRgb,
        NrdApi.Format.R16_UNORM => Format.R16_UNorm,
        NrdApi.Format.R16_SNORM => Format.R16_SNorm,
        NrdApi.Format.R16_UINT => Format.R16_UInt,
        NrdApi.Format.R16_SINT => Format.R16_SInt,
        NrdApi.Format.R16_SFLOAT => Format.R16_Float,
        NrdApi.Format.RG16_UNORM => Format.R16G16_UNorm,
        NrdApi.Format.RG16_SNORM => Format.R16G16_SNorm,
        NrdApi.Format.RG16_UINT => Format.R16G16_UInt,
        NrdApi.Format.RG16_SINT => Format.R16G16_SInt,
        NrdApi.Format.RG16_SFLOAT => Format.R16G16_Float,
        NrdApi.Format.RGBA16_UNORM => Format.R16G16B16A16_UNorm,
        NrdApi.Format.RGBA16_SNORM => Format.R16G16B16A16_SNorm,
        NrdApi.Format.RGBA16_UINT => Format.R16G16B16A16_UInt,
        NrdApi.Format.RGBA16_SINT => Format.R16G16B16A16_SInt,
        NrdApi.Format.RGBA16_SFLOAT => Format.R16G16B16A16_Float,
        NrdApi.Format.R32_UINT => Format.R32_UInt,
        NrdApi.Format.R32_SINT => Format.R32_SInt,
        NrdApi.Format.R32_SFLOAT => Format.R32_Float,
        NrdApi.Format.RG32_UINT => Format.R32G32_UInt,
        NrdApi.Format.RG32_SINT => Format.R32G32_SInt,
        NrdApi.Format.RG32_SFLOAT => Format.R32G32_Float,
        NrdApi.Format.RGBA32_UINT => Format.R32G32B32A32_UInt,
        NrdApi.Format.RGBA32_SINT => Format.R32G32B32A32_SInt,
        NrdApi.Format.RGBA32_SFLOAT => Format.R32G32B32A32_Float,
        NrdApi.Format.R10_G10_B10_A2_UNORM => Format.R10G10B10A2_UNorm,
        NrdApi.Format.R10_G10_B10_A2_UINT => Format.R10G10B10A2_UInt,
        NrdApi.Format.R11_G11_B10_UFLOAT => Format.R11G11B10_Float,
        _ => throw new NotSupportedException($"NRD format {f} not mapped to DXGI"),
    };

    // ---- per-frame denoise ----
    // The caller supplies the input/output textures NRD references by ResourceType (IN_MV, IN_NORMAL_ROUGHNESS,
    // IN_VIEWZ, IN_DIFF_RADIANCE_HITDIST, OUT_DIFF_RADIANCE_HITDIST). We set common+denoiser settings, ask NRD for
    // the dispatch list, then for each dispatch: resolve resources (snapshot or pool), barrier them to SRV/UAV,
    // write CPU descriptors into the GPU heap, upload constants, bind and dispatch. All recorded onto `cl`.
    public sealed class Resource {
        public ID3D12Resource Tex; public Format Format; public ResourceStates State;
        public Resource(ID3D12Resource t, Format f, ResourceStates s) { Tex = t; Format = f; State = s; }
    }

    public unsafe void Denoise(ID3D12GraphicsCommandList cl, in NrdSettings.NrdCommonSettings common,
                               in NrdSettings.ReblurSettings reblur, Resource[] snapshot) {
        if (!Available) return;

        NrdApi.SetCommonSettings(instance, in common);
        fixed (NrdSettings.ReblurSettings* rp = &reblur)
            NrdApi.SetDenoiserSettings(instance, Identifier, (IntPtr)rp);

        uint id = Identifier;
        if (NrdApi.GetComputeDispatches(instance, (IntPtr)(&id), 1, out IntPtr ddPtr, out uint ddNum) != NrdApi.Result.Success)
            return;

        cl.SetDescriptorHeaps(srvUavHeap);
        cl.SetComputeRootSignature(rootSig);

        var dispatches = (NrdApi.DispatchDesc*)ddPtr;
        for (uint d = 0; d < ddNum; d++) {
            NrdApi.DispatchDesc dd = dispatches[d];
            PipelineLayout layout = pipelineLayouts[dd.PipelineIndex];
            var resPtr = (NrdApi.ResourceDesc*)dd.Resources;

            // The table layout is fixed: SRV slots [0, perSetTexturesMax) then UAV slots [perSetTexturesMax, +perSetStorageMax).
            // We bump-allocate a contiguous block in the heap for THIS dispatch's table and write each descriptor at
            // its computed slot. NRD's resources[] are concatenated per range (TEXTURE range first, then STORAGE).
            int srvMax = (int)instDesc.DescriptorPoolDesc.PerSetTexturesMaxNum;
            int uavMax = (int)instDesc.DescriptorPoolDesc.PerSetStorageTexturesMaxNum;
            int tableSize = srvMax + uavMax;
            if (srvUavHeapCursor + tableSize > srvUavHeapCapacity) srvUavHeapCursor = 0;   // ring (within-frame headroom)
            int tableBase = srvUavHeapCursor;
            srvUavHeapCursor += tableSize;

            int n = 0, srvSlot = 0, uavSlot = 0;
            var preBarriers = new List<ResourceBarrier>();
            foreach (var range in layout.Ranges) {
                bool isStorage = range.DescriptorType == NrdApi.DescriptorType.STORAGE_TEXTURE;
                for (uint j = 0; j < range.DescriptorsNum; j++) {
                    NrdApi.ResourceDesc rd = resPtr[n++];
                    Resource res = Resolve(rd, snapshot);

                    ResourceStates want = isStorage ? ResourceStates.UnorderedAccess : ResourceStates.NonPixelShaderResource;
                    if (res.State != want) {
                        preBarriers.Add(ResourceBarrier.BarrierTransition(res.Tex, res.State, want));
                        res.State = want;
                    } else if (isStorage) {
                        preBarriers.Add(ResourceBarrier.BarrierUnorderedAccessView(res.Tex));   // UAV→UAV hazard
                    }

                    int slot = isStorage ? (srvMax + uavSlot++) : srvSlot++;
                    CpuDescriptorHandle h = srvUavHeap.GetCPUDescriptorHandleForHeapStart();
                    h.Ptr += (nuint)((tableBase + slot) * srvUavInc);
                    if (isStorage)
                        dev.Device.CreateUnorderedAccessView(res.Tex, null,
                            new UnorderedAccessViewDescription { Format = res.Format, ViewDimension = UnorderedAccessViewDimension.Texture2D }, h);
                    else
                        dev.Device.CreateShaderResourceView(res.Tex,
                            new ShaderResourceViewDescription {
                                Format = res.Format, ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                                Shader4ComponentMapping = ShaderComponentMapping.Default,
                                Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
                            }, h);
                }
            }
            if (preBarriers.Count > 0) cl.ResourceBarrier(preBarriers.ToArray());

            // Upload constants into the ring (NRD says when it differs from the previous dispatch).
            ulong cbGpu = cbLastGpu;
            if (dd.ConstantBufferDataSize > 0 && !dd.ConstantBufferDataMatchesPreviousDispatch) {
                if (cbCursor + cbViewSize > cbRingSize) cbCursor = 0;
                Buffer.MemoryCopy((void*)dd.ConstantBufferData, cbMapped + cbCursor, dd.ConstantBufferDataSize, dd.ConstantBufferDataSize);
                cbGpu = cb.GPUVirtualAddress + (ulong)cbCursor;
                cbCursor += cbViewSize;
                cbLastGpu = cbGpu;
            }

            cl.SetPipelineState(pipelines[dd.PipelineIndex]);
            cl.SetComputeRootConstantBufferView(0, cbGpu);
            GpuDescriptorHandle gpu = srvUavHeap.GetGPUDescriptorHandleForHeapStart();
            gpu.Ptr += (ulong)(tableBase * srvUavInc);
            cl.SetComputeRootDescriptorTable(1, gpu);
            cl.Dispatch(dd.GridWidth, dd.GridHeight, 1);
        }
    }

    ulong cbLastGpu;

    Resource Resolve(NrdApi.ResourceDesc rd, Resource[] snapshot) {
        if (rd.Type == NrdApi.ResourceType.TRANSIENT_POOL) return PoolResource(permanentPoolSize + rd.IndexInPool);
        if (rd.Type == NrdApi.ResourceType.PERMANENT_POOL) return PoolResource(rd.IndexInPool);
        Resource r = snapshot[(int)rd.Type];
        if (r == null) throw new InvalidOperationException($"NRD requested unbound resource {rd.Type}");
        return r;
    }

    // Wrap a pool texture as a Resource view (state lives in the PoolTex; mirror it back after).
    Resource[] poolResourceCache;
    Resource PoolResource(int idx) {
        poolResourceCache ??= new Resource[pool.Length];
        if (poolResourceCache[idx] == null)
            poolResourceCache[idx] = new Resource(pool[idx].Resource, pool[idx].Format, pool[idx].State);
        else
            poolResourceCache[idx].State = pool[idx].State;   // sync in
        return poolResourceCache[idx];
    }

    // After Denoise, pool states changed on the wrapper — mirror them back so next frame's barriers are correct.
    public void SyncPoolStates() {
        if (poolResourceCache == null) return;
        for (int i = 0; i < pool.Length; i++)
            if (poolResourceCache[i] != null) pool[i].State = poolResourceCache[i].State;
    }

    public void ResetFrameRings() { srvUavHeapCursor = 0; }

    // Synthetic smoke test: allocate dummy inputs/output, run ONE full Denoise on the GPU, prove all dispatches
    // execute with no D3D12 validation error. Does NOT check output correctness — just that the graph runs.
    public bool DenoiseSelfTest(int w, int h) {
        Resource Mk(Format f, ResourceStates st) {
            var rd = ResourceDescription.Texture2D(f, (uint)w, (uint)h, 1, 1);
            rd.Flags = ResourceFlags.AllowUnorderedAccess;
            var t = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None, rd, st);
            return new Resource(t, f, st);
        }
        var snapshot = new Resource[(int)NrdApi.ResourceType.MAX_NUM];
        snapshot[(int)NrdApi.ResourceType.IN_MV] = Mk(Format.R16G16B16A16_Float, ResourceStates.NonPixelShaderResource);
        snapshot[(int)NrdApi.ResourceType.IN_NORMAL_ROUGHNESS] = Mk(Format.R10G10B10A2_UNorm, ResourceStates.NonPixelShaderResource);
        snapshot[(int)NrdApi.ResourceType.IN_VIEWZ] = Mk(Format.R16_Float, ResourceStates.NonPixelShaderResource);
        snapshot[(int)NrdApi.ResourceType.IN_DIFF_RADIANCE_HITDIST] = Mk(Format.R16G16B16A16_Float, ResourceStates.NonPixelShaderResource);
        snapshot[(int)NrdApi.ResourceType.OUT_DIFF_RADIANCE_HITDIST] = Mk(Format.R16G16B16A16_Float, ResourceStates.UnorderedAccess);

        var common = NrdSettings.NrdCommonSettings.Default();
        // Minimal sane matrices: identity view/clip (NRD needs them non-degenerate). Resolution = rect = full.
        unsafe {
            common.ViewToClipMatrix[0] = 1f; common.ViewToClipMatrix[5] = 1f; common.ViewToClipMatrix[10] = 1f; common.ViewToClipMatrix[15] = 1f;
            common.ViewToClipMatrixPrev[0] = 1f; common.ViewToClipMatrixPrev[5] = 1f; common.ViewToClipMatrixPrev[10] = 1f; common.ViewToClipMatrixPrev[15] = 1f;
            common.WorldToViewMatrix[0] = 1f; common.WorldToViewMatrix[5] = 1f; common.WorldToViewMatrix[10] = 1f; common.WorldToViewMatrix[15] = 1f;
            common.WorldToViewMatrixPrev[0] = 1f; common.WorldToViewMatrixPrev[5] = 1f; common.WorldToViewMatrixPrev[10] = 1f; common.WorldToViewMatrixPrev[15] = 1f;
            common.ResourceSize[0] = (ushort)w; common.ResourceSize[1] = (ushort)h;
            common.ResourceSizePrev[0] = (ushort)w; common.ResourceSizePrev[1] = (ushort)h;
            common.RectSize[0] = (ushort)w; common.RectSize[1] = (ushort)h;
            common.RectSizePrev[0] = (ushort)w; common.RectSizePrev[1] = (ushort)h;
        }
        common.AccumulationMode = NrdApi.AccumulationMode.CLEAR_AND_RESTART;   // first frame, no history
        var reblur = NrdSettings.ReblurSettings.Default();

        try {
            ResetFrameRings();
            var c = common; var rb = reblur;   // copy out of `in`/locals so the lambda can capture by value
            dev.ExecuteSync(cl => Denoise(cl, c, rb, snapshot));
            SyncPoolStates();
            return true;
        } finally {
            foreach (var r in snapshot) r?.Tex?.Dispose();
        }
    }

    public void Dispose() {
        Available = false;
        if (pipelines != null) foreach (var p in pipelines) p?.Dispose();
        pipelines = null;
        if (pool != null) foreach (var t in pool) t.Resource?.Dispose();
        pool = null;
        rootSig?.Dispose(); rootSig = null;
        srvUavHeap?.Dispose(); srvUavHeap = null;
        if (cb != null) { cb.Unmap(0); cb.Dispose(); cb = null; }
        if (instance != IntPtr.Zero) { NrdApi.DestroyInstance(instance); instance = IntPtr.Zero; }
    }
}
