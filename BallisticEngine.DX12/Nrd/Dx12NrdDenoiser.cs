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
    PoolTex[] pool;                        // permanent pool first, then transient
    int permanentPoolSize;
    NrdApi.InstanceDesc instDesc;

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
