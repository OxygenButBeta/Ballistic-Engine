using System;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// Hi-Z (hierarchical depth) pyramid for the DX12 GPU-driven occlusion cull (port of GLHiZPass). A full
// mip chain of an R32_Float texture, each coarser texel = the MAX (farthest) window-depth of its 2x2
// footprint — the conservative reduction so the cull can NEVER false-cull. Built from the PREVIOUS frame's
// G-buffer depth (the cull runs before this frame's depth exists) via compute: CSCopy fills mip0 from the
// depth SRV; CSDownsample MAX-reduces each level (read/write per-mip UAVs, a UAV barrier orders them).
// One heap holds: slot 0 = the all-mips SRV (the cull samples it), slot 1 = the depth SRV (build input),
// slots 2.. = one UAV per mip (build outputs).
public sealed class Dx12HiZ : IDisposable {
    readonly Dx12Device dev;
    ID3D12Resource pyramid;
    int width, height, mipCount;
    ResourceStates state = ResourceStates.NonPixelShaderResource;

    ID3D12RootSignature copyRootSig, downRootSig;
    ID3D12PipelineState copyPso, downPso;
    Dx12DescriptorHeap heap;        // 0=all-mips SRV, 1=depth SRV, 2..=per-mip UAVs
    ID3D12Resource downCb; unsafe byte* downCbMapped; int downCbSlot;
    const int SrvAllMips = 0, SrvDepth = 1, UavBase = 2;

    public int MipCount => mipCount;
    public int Width => width;
    public int Height => height;
    public Dx12DescriptorHeap Heap => heap;
    public GpuDescriptorHandle CullSrvGpu => heap.Gpu(SrvAllMips);   // bound as the cull's HiZ SRV table

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct DownParams { public uint SrcW, SrcH, DstW, DstH; }

    public Dx12HiZ(Dx12Device device) { dev = device; BuildPipelines(); }

    unsafe void BuildPipelines() {
        // Copy: SRV table (depth t0) + UAV table (mip0 u0).
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 0);
        var uav1Range = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 0);
        copyRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] {
                new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.All),
                new RootParameter1(new RootDescriptorTable1(uav1Range), ShaderVisibility.All),
            })));
        // Downsample: CBV b0 + UAV table (src u0 + dst u1).
        var uav2Range = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 2, baseShaderRegister: 0);
        downRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] {
                new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All),
                new RootParameter1(new RootDescriptorTable1(uav2Range), ShaderVisibility.All),
            })));

        string hlsl = EmbeddedShaderSource.ReadHlsl("HiZBuild.hlsl");
        copyPso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription {
            RootSignature = copyRootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSCopy", "HiZBuild.hlsl"),
        });
        downPso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription {
            RootSignature = downRootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSDownsample", "HiZBuild.hlsl"),
        });

        int slot = (System.Runtime.InteropServices.Marshal.SizeOf<DownParams>() + 255) & ~255;
        downCbSlot = slot;
        downCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(slot * 32)), ResourceStates.GenericRead);   // up to 32 mips
        downCbMapped = downCb.Map<byte>(0);
    }

    // Create the all-mips SRV of the pyramid into an external CPU descriptor handle (for the cull's bindless
    // read — the pyramid is sampled via ResourceDescriptorHeap[index] from Dx12Backend.BindlessHeap).
    public void CreateAllMipsSrv(CpuDescriptorHandle dst) {
        dev.Device.CreateShaderResourceView(pyramid, new ShaderResourceViewDescription {
            Format = Format.R32_Float, ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = (uint)mipCount },
        }, dst);
    }

    // Drop the pyramid so the next Ensure() rebuilds it (returns true → caller re-points its bindless SRV) and
    // its first Build() refills it from the NEW depth. Called on a scene swap: a same-resolution swap leaves
    // Ensure() a no-op, so without this the pyramid keeps the OLD scene's depth and the occlusion cull rejects
    // the new scene's geometry behind stale occluders (the "culling breaks after switching scenes" bug).
    public void Invalidate() {
        pyramid?.Dispose(); heap?.Dispose();
        pyramid = null; heap = null;
        width = height = mipCount = 0;
    }

    // Returns true if the pyramid was (re)created (the caller must re-register any external SRV).
    public bool Ensure(int w, int h) {
        if (pyramid != null && w == width && h == height) return false;
        pyramid?.Dispose(); heap?.Dispose();
        width = w; height = h;
        mipCount = 1 + (int)MathF.Floor(MathF.Log2(Math.Max(w, h)));

        var desc = ResourceDescription.Texture2D(Format.R32_Float, (uint)w, (uint)h, mipLevels: (ushort)mipCount, arraySize: 1);
        desc.Flags = ResourceFlags.AllowUnorderedAccess;
        pyramid = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            desc, ResourceStates.NonPixelShaderResource);
        pyramid.Name = "HiZPyramid";
        state = ResourceStates.NonPixelShaderResource;

        heap = new Dx12DescriptorHeap(dev, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            UavBase + mipCount, shaderVisible: true);
        // slot 0: all-mips SRV (cull samples this).
        dev.Device.CreateShaderResourceView(pyramid, new ShaderResourceViewDescription {
            Format = Format.R32_Float, ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = (uint)mipCount },
        }, heap.Cpu(SrvAllMips));
        // slot 1: depth SRV (filled per build from the G-buffer depth descriptor).
        // slots 2..: one UAV per mip.
        for (int mip = 0; mip < mipCount; mip++) {
            dev.Device.CreateUnorderedAccessView(pyramid, null, new UnorderedAccessViewDescription {
                Format = Format.R32_Float, ViewDimension = UnorderedAccessViewDimension.Texture2D,
                Texture2D = new Texture2DUnorderedAccessView { MipSlice = (uint)mip },
            }, heap.Cpu(UavBase + mip));
        }
        return true;
    }

    // Build the pyramid from the (compute-readable) G-buffer depth. Records into `cl`. Leaves the pyramid in
    // NonPixelShaderResource so the cull samples it. The G-buffer depth must already be NonPixelShaderResource.
    public unsafe void Build(ID3D12GraphicsCommandList4 cl, CpuDescriptorHandle depthSrvCpu) {
        // Mirror the current depth descriptor into heap slot 1.
        dev.Device.CopyDescriptorsSimple(1, heap.Cpu(SrvDepth), depthSrvCpu,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

        cl.SetDescriptorHeaps(heap.Heap);
        if (state != ResourceStates.UnorderedAccess) {
            cl.ResourceBarrierTransition(pyramid, state, ResourceStates.UnorderedAccess);
            state = ResourceStates.UnorderedAccess;
        }
        // mip0 = copy depth.
        cl.SetComputeRootSignature(copyRootSig);
        cl.SetPipelineState(copyPso);
        cl.SetComputeRootDescriptorTable(0, heap.Gpu(SrvDepth));
        cl.SetComputeRootDescriptorTable(1, heap.Gpu(UavBase));
        cl.Dispatch((uint)((width + 7) / 8), (uint)((height + 7) / 8), 1);

        // mips 1..N: MAX downsample (read mip-1 UAV, write mip UAV), UAV barrier between.
        int srcW = width, srcH = height;
        for (int mip = 1; mip < mipCount; mip++) {
            int dstW = Math.Max(1, srcW / 2), dstH = Math.Max(1, srcH / 2);
            *(DownParams*)(downCbMapped + (long)mip * downCbSlot) = new DownParams {
                SrcW = (uint)srcW, SrcH = (uint)srcH, DstW = (uint)dstW, DstH = (uint)dstH,
            };
            cl.ResourceBarrierUnorderedAccessView(pyramid);
            cl.SetComputeRootSignature(downRootSig);
            cl.SetPipelineState(downPso);
            cl.SetComputeRootConstantBufferView(0, downCb.GPUVirtualAddress + (ulong)((long)mip * downCbSlot));
            cl.SetComputeRootDescriptorTable(1, heap.Gpu(UavBase + mip - 1));   // src=mip-1, dst=mip (contiguous)
            cl.Dispatch((uint)((dstW + 7) / 8), (uint)((dstH + 7) / 8), 1);
            srcW = dstW; srcH = dstH;
        }

        cl.ResourceBarrierTransition(pyramid, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
        state = ResourceStates.NonPixelShaderResource;
    }

    public void Dispose() {
        copyRootSig?.Dispose(); downRootSig?.Dispose(); copyPso?.Dispose(); downPso?.Dispose();
        downCb?.Dispose(); heap?.Dispose(); pyramid?.Dispose();
    }
}
