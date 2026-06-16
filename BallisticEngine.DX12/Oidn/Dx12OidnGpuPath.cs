using System;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// Zero-copy GPU OIDN denoise (no CPU readback). A D3D12 SHARED float4 buffer is imported by OIDN's HIP
// device (same physical GPU, matched by LUID); the half-res RGBA16F GI texture is packed into it on the
// GPU (CSPack, half->float), OIDN denoises it IN PLACE on the GPU, and it's unpacked back to a texture
// (CSUnpack, float->half). This eliminates the CPU half<->float conversion loops + the readback/upload
// heaps + the GPU stall that made the readback path ~60ms — and unlike a HALF-format OIDN denoise it keeps
// FLOAT precision, so the quality matches the readback path. Falls back (caller's responsibility) to the
// CPU readback if the denoiser isn't HIP/share-capable.
//
// Cross-API sync is implicit: each pack/unpack runs in its own ExecuteSync (full GPU idle), and the
// denoiser's ExecuteShared calls oidnSyncDevice — so writes are complete before the next reader. D3D12
// buffer state is handled by implicit promotion/decay (buffers decay to COMMON after each idled submit).
public sealed class Dx12OidnGpuPath : IDisposable {
    readonly Dx12Device dev;
    ID3D12RootSignature packRootSig, unpackRootSig, packAuxRootSig;
    ID3D12PipelineState packPso, unpackPso, packAuxPso;
    ID3D12Resource cb; unsafe byte* cbMapped;
    ID3D12Resource auxCb; unsafe byte* auxCbMapped;

    ID3D12Resource sharedBuf;       // D3D12 SHARED float4 buffer aliased by OIDN
    IntPtr sharedHandle;
    Dx12DescriptorHeap heap;        // 0=srcTex SRV, 1=buf UAV, 2=buf SRV, 3=dstTex UAV
    int w, h;
    bool ready;
    static int shareSeq;            // process-wide counter → unique shared-handle names (avoid 0x887A002C)
    const int SrvSrcTex = 0, UavBuf = 1, SrvBuf = 2, UavDstTex = 3;

    // P6.1 GUIDED denoise: two more SHARED float4 buffers holding half-res albedo + normal AOVs (packed from the
    // G-buffer by CSPackAux), imported by OIDN as the "albedo"/"normal" guide images. Only created when the
    // caller requests guided denoise (EnsureAux); the unguided path is byte-untouched. Their descriptors live in
    // a separate aux heap: 0=GAlbedo SRV (t2), 1=GNormal SRV (t3), 2=albedoBuf UAV (u2), 3=normalBuf UAV (u3).
    ID3D12Resource albedoBuf, normalBuf;
    IntPtr albedoHandle, normalHandle;
    Dx12DescriptorHeap auxHeap;
    bool auxReady;
    const int SrvGAlbedo = 0, SrvGNormal = 1, UavAlbedoBuf = 2, UavNormalBuf = 3;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct Dims { public uint W, H, P0, P1; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct AuxDims { public uint W, H; public float SX, SY; }

    public bool Ready => ready;
    public bool AuxReady => auxReady;
    public IntPtr AlbedoHandle => albedoHandle;
    public IntPtr NormalHandle => normalHandle;
    public ulong AuxByteSize => (ulong)((long)w * h * 16);

    public Dx12OidnGpuPath(Dx12Device device) { dev = device; BuildPipelines(); }

    unsafe void BuildPipelines() {
        // Pack: CBV b0 + SRV table(t0) + UAV table(u0). Unpack: CBV b0 + SRV table(t1) + UAV table(u1).
        ID3D12RootSignature MakeRootSig(uint srvReg, uint uavReg) {
            var srv = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: srvReg);
            var uav = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: uavReg);
            return dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
                new RootSignatureDescription1(RootSignatureFlags.None, new[] {
                    new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All),
                    new RootParameter1(new RootDescriptorTable1(srv), ShaderVisibility.All),
                    new RootParameter1(new RootDescriptorTable1(uav), ShaderVisibility.All),
                })));
        }
        packRootSig = MakeRootSig(0u, 0u);
        unpackRootSig = MakeRootSig(1u, 1u);

        // PackAux: CBV b1 (AuxDims) + SRV table {t2 GAlbedo, t3 GNormal} + UAV table {u2 albedoBuf, u3 normalBuf}.
        var auxSrv = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 2, baseShaderRegister: 2);   // t2,t3
        var auxUav = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 2, baseShaderRegister: 2);   // u2,u3
        packAuxRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] {
                new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.All),  // b1 AuxDims
                new RootParameter1(new RootDescriptorTable1(auxSrv), ShaderVisibility.All),
                new RootParameter1(new RootDescriptorTable1(auxUav), ShaderVisibility.All),
            })));

        string hlsl = EmbeddedShaderSource.ReadHlsl("OidnPackUnpack.hlsl");
        packPso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription {
            RootSignature = packRootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSPack", "OidnPackUnpack.hlsl"),
        });
        unpackPso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription {
            RootSignature = unpackRootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSUnpack", "OidnPackUnpack.hlsl"),
        });
        packAuxPso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription {
            RootSignature = packAuxRootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSPackAux", "OidnPackUnpack.hlsl"),
        });

        int slot = (System.Runtime.InteropServices.Marshal.SizeOf<Dims>() + 255) & ~255;
        cb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)slot), ResourceStates.GenericRead);
        cbMapped = cb.Map<byte>(0);
        int aslot = (System.Runtime.InteropServices.Marshal.SizeOf<AuxDims>() + 255) & ~255;
        auxCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)aslot), ResourceStates.GenericRead);
        auxCbMapped = auxCb.Map<byte>(0);
    }

    // (Re)create the shared buffer + OIDN import + descriptors for the given size. Returns false (→ caller
    // uses the CPU readback) if the OIDN import fails. dstTex is the half-res RGBA16F denoise target whose
    // UAV the unpack writes (created with allowUav); it's stable, so its UAV is built once here.
    public unsafe bool Ensure(Dx12OidnDenoiser oidn, ID3D12Resource dstTex, int width, int height) {
        if (ready && width == w && height == h) return true;
        ReleaseShared();
        w = width; h = height;
        ulong bytes = (ulong)((long)w * h * 16);   // float4 per pixel, tightly packed

        var bufDesc = ResourceDescription.Buffer(bytes, ResourceFlags.AllowUnorderedAccess);
        sharedBuf = dev.Device.CreateCommittedResource(
            HeapProperties.DefaultHeapProperties, HeapFlags.Shared, bufDesc, ResourceStates.Common);
        sharedBuf.Name = "OidnSharedFloatBuf";
        // The shared-handle name MUST be unique per process+instance: a FIXED name ("BallisticOidnSharedFloat")
        // fails with DXGI_ERROR_NAME_ALREADY_EXISTS (0x887A002C) if a prior/concurrent process still holds it, and
        // also if Ensure() re-creates the buffer on resize before the old handle is closed. The name is only used
        // to OPEN the handle by name (which we never do — we import the IntPtr directly), so it can be anything
        // unique; PID + a monotonic counter guarantees no collision across runs or within a run.
        string shareName = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"BallisticOidnSharedFloat_{Environment.ProcessId}_{System.Threading.Interlocked.Increment(ref shareSeq)}");
        sharedHandle = dev.Device.CreateSharedHandle(sharedBuf, null, shareName);
        if (sharedHandle == IntPtr.Zero) { ReleaseShared(); return false; }
        // OIDN reads it as FLOAT3 (skip the .a), pixelByteStride 16, rowByteStride W*16.
        if (!oidn.ImportSharedBuffer(sharedHandle, bytes, w, h, w * 16)) { ReleaseShared(); return false; }

        heap = new Dx12DescriptorHeap(dev, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 4, shaderVisible: true);
        int n = w * h;
        dev.Device.CreateUnorderedAccessView(sharedBuf, null, new UnorderedAccessViewDescription {
            Format = Format.Unknown, ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = (uint)n, StructureByteStride = 16 },
        }, heap.Cpu(UavBuf));
        dev.Device.CreateShaderResourceView(sharedBuf, new ShaderResourceViewDescription {
            Format = Format.Unknown, ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Buffer,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)n, StructureByteStride = 16 },
        }, heap.Cpu(SrvBuf));
        dev.Device.CreateUnorderedAccessView(dstTex, null, new UnorderedAccessViewDescription {
            Format = Format.R16G16B16A16_Float, ViewDimension = UnorderedAccessViewDimension.Texture2D,
            Texture2D = new Texture2DUnorderedAccessView { MipSlice = 0 },
        }, heap.Cpu(UavDstTex));

        *(Dims*)cbMapped = new Dims { W = (uint)w, H = (uint)h };
        ready = true;
        return true;
    }

    // P6.1: (re)create the two AUX shared buffers (albedo, normal) + the aux descriptor heap, sized to the
    // current half-res (call AFTER Ensure). Returns false → caller denoises UNGUIDED (color only). The aux
    // SHARED HANDLES are then imported by the OIDN denoiser (ImportAuxBuffers) so OIDN reads them as guide AOVs.
    public unsafe bool EnsureAux() {
        if (!ready) return false;
        if (auxReady) return true;
        ulong bytes = AuxByteSize;
        var bufDesc = ResourceDescription.Buffer(bytes, ResourceFlags.AllowUnorderedAccess);
        albedoBuf = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.Shared, bufDesc, ResourceStates.Common);
        normalBuf = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.Shared, bufDesc, ResourceStates.Common);
        albedoBuf.Name = "OidnSharedAlbedoBuf"; normalBuf.Name = "OidnSharedNormalBuf";
        string an = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"BallisticOidnAlbedo_{Environment.ProcessId}_{System.Threading.Interlocked.Increment(ref shareSeq)}");
        string nn = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"BallisticOidnNormal_{Environment.ProcessId}_{System.Threading.Interlocked.Increment(ref shareSeq)}");
        albedoHandle = dev.Device.CreateSharedHandle(albedoBuf, null, an);
        normalHandle = dev.Device.CreateSharedHandle(normalBuf, null, nn);
        if (albedoHandle == IntPtr.Zero || normalHandle == IntPtr.Zero) { ReleaseAux(); return false; }

        auxHeap = new Dx12DescriptorHeap(dev, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 4, shaderVisible: true);
        // SRV slots (GAlbedo/GNormal) are filled per-frame in PackAux (the G-buffer SRVs). The two buffer UAVs
        // are stable, built once here.
        int n = w * h;
        dev.Device.CreateUnorderedAccessView(albedoBuf, null, new UnorderedAccessViewDescription {
            Format = Format.Unknown, ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = (uint)n, StructureByteStride = 16 },
        }, auxHeap.Cpu(UavAlbedoBuf));
        dev.Device.CreateUnorderedAccessView(normalBuf, null, new UnorderedAccessViewDescription {
            Format = Format.Unknown, ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = (uint)n, StructureByteStride = 16 },
        }, auxHeap.Cpu(UavNormalBuf));
        auxReady = true;
        return true;
    }

    // P6.1 GPU pack of the AOV guides: full-res G-buffer albedo + normal SRVs -> the two half-res aux buffers.
    // fullW/fullH = the G-buffer resolution; the shader maps each half-res texel to its full-res sample.
    public unsafe void PackAux(CpuDescriptorHandle gAlbedoSrv, CpuDescriptorHandle gNormalSrv, int fullW, int fullH) {
        if (!auxReady) return;
        *(AuxDims*)auxCbMapped = new AuxDims { W = (uint)w, H = (uint)h, SX = (float)fullW / w, SY = (float)fullH / h };
        var hType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        dev.Device.CopyDescriptorsSimple(1, auxHeap.Cpu(SrvGAlbedo), gAlbedoSrv, hType);
        dev.Device.CopyDescriptorsSimple(1, auxHeap.Cpu(SrvGNormal), gNormalSrv, hType);
        dev.ExecuteSync(cl => {
            cl.SetDescriptorHeaps(auxHeap.Heap);
            cl.SetComputeRootSignature(packAuxRootSig);
            cl.SetPipelineState(packAuxPso);
            cl.SetComputeRootConstantBufferView(0, auxCb.GPUVirtualAddress);
            cl.SetComputeRootDescriptorTable(1, auxHeap.Gpu(SrvGAlbedo));   // t2,t3
            cl.SetComputeRootDescriptorTable(2, auxHeap.Gpu(UavAlbedoBuf)); // u2,u3
            cl.Dispatch((uint)((w + 7) / 8), (uint)((h + 7) / 8), 1);
        });
    }

    // Public so the renderer can reclaim the aux buffers immediately if the OIDN aux IMPORT fails after
    // EnsureAux already allocated them (else they'd sit unused until the next resize/Dispose — audit nit).
    public void ReleaseAux() {
        auxReady = false;
        auxHeap?.Dispose(); auxHeap = null;
        if (albedoHandle != IntPtr.Zero) { CloseHandle(albedoHandle); albedoHandle = IntPtr.Zero; }
        if (normalHandle != IntPtr.Zero) { CloseHandle(normalHandle); normalHandle = IntPtr.Zero; }
        albedoBuf?.Dispose(); albedoBuf = null;
        normalBuf?.Dispose(); normalBuf = null;
    }

    // GPU pack: srcTexSrv (the GI texture's SRV, in a NonPixelShaderResource state) -> shared float buffer.
    public unsafe void Pack(CpuDescriptorHandle srcTexSrv) {
        dev.Device.CopyDescriptorsSimple(1, heap.Cpu(SrvSrcTex), srcTexSrv,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        dev.ExecuteSync(cl => {
            cl.SetDescriptorHeaps(heap.Heap);
            cl.SetComputeRootSignature(packRootSig);
            cl.SetPipelineState(packPso);
            cl.SetComputeRootConstantBufferView(0, cb.GPUVirtualAddress);
            cl.SetComputeRootDescriptorTable(1, heap.Gpu(SrvSrcTex));
            cl.SetComputeRootDescriptorTable(2, heap.Gpu(UavBuf));
            cl.Dispatch((uint)((w + 7) / 8), (uint)((h + 7) / 8), 1);
        });
    }

    // GPU unpack: shared float buffer -> the dst texture (must already be in UnorderedAccess).
    public unsafe void Unpack() {
        dev.ExecuteSync(cl => {
            cl.SetDescriptorHeaps(heap.Heap);
            cl.SetComputeRootSignature(unpackRootSig);
            cl.SetPipelineState(unpackPso);
            cl.SetComputeRootConstantBufferView(0, cb.GPUVirtualAddress);
            cl.SetComputeRootDescriptorTable(1, heap.Gpu(SrvBuf));
            cl.SetComputeRootDescriptorTable(2, heap.Gpu(UavDstTex));
            cl.Dispatch((uint)((w + 7) / 8), (uint)((h + 7) / 8), 1);
        });
    }

    void ReleaseShared() {
        ready = false;
        ReleaseAux();   // aux buffers are sized to the color buffer → tear down together on resize
        heap?.Dispose(); heap = null;
        if (sharedHandle != IntPtr.Zero) { CloseHandle(sharedHandle); sharedHandle = IntPtr.Zero; }
        sharedBuf?.Dispose(); sharedBuf = null;
    }

    [System.Runtime.InteropServices.DllImport("kernel32", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    public void Dispose() {
        ReleaseShared();
        packRootSig?.Dispose(); unpackRootSig?.Dispose(); packAuxRootSig?.Dispose();
        packPso?.Dispose(); unpackPso?.Dispose(); packAuxPso?.Dispose();
        cb?.Dispose(); auxCb?.Dispose();
    }
}
