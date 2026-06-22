using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

public sealed class Dx12HiZ : IDisposable {
    readonly Dx12Device dev;
    ID3D12Resource pyramid;
    int width, height, mipCount;
    ResourceStates state = ResourceStates.NonPixelShaderResource;

    ID3D12RootSignature copyRootSig, downRootSig;
    ID3D12PipelineState copyPso, downPso;
    Dx12DescriptorHeap heap;
    ID3D12Resource downCb; unsafe byte* downCbMapped; int downCbSlot;
    const int SrvAllMips = 0, SrvDepth = 1, UavBase = 2;

    public int MipCount => mipCount;
    public int Width => width;
    public int Height => height;
    public Dx12DescriptorHeap Heap => heap;
    public GpuDescriptorHandle CullSrvGpu => heap.Gpu(SrvAllMips);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct DownParams { public uint SrcW, SrcH, DstW, DstH; }

    public Dx12HiZ(Dx12Device device) { dev = device; BuildPipelines(); }

    unsafe void BuildPipelines() {
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 0);
        var uav1Range = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 0);
        copyRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] {
                new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.All),
                new RootParameter1(new RootDescriptorTable1(uav1Range), ShaderVisibility.All),
            })));
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
            ResourceDescription.Buffer((ulong)(slot * 32)), ResourceStates.GenericRead);
        downCbMapped = downCb.Map<byte>(0);
    }

    public void CreateAllMipsSrv(CpuDescriptorHandle dst) {
        dev.Device.CreateShaderResourceView(pyramid, new ShaderResourceViewDescription {
            Format = Format.R32_Float, ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = (uint)mipCount },
        }, dst);
    }

    public void Invalidate() {
        pyramid?.Dispose(); heap?.Dispose();
        pyramid = null; heap = null;
        width = height = mipCount = 0;
    }

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
            UavBase + mipCount, shaderVisible: true, framesInFlight: dev.FramesInFlight);
        for (int slab = 0; slab < dev.FramesInFlight; slab++) {
            int b = slab * (UavBase + mipCount);
            dev.Device.CreateShaderResourceView(pyramid, new ShaderResourceViewDescription {
                Format = Format.R32_Float, ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = (uint)mipCount },
            }, heap.CpuPhysical(b + SrvAllMips));
            for (int mip = 0; mip < mipCount; mip++) {
                dev.Device.CreateUnorderedAccessView(pyramid, null, new UnorderedAccessViewDescription {
                    Format = Format.R32_Float, ViewDimension = UnorderedAccessViewDimension.Texture2D,
                    Texture2D = new Texture2DUnorderedAccessView { MipSlice = (uint)mip },
                }, heap.CpuPhysical(b + UavBase + mip));
            }
        }
        return true;
    }

    public unsafe void Build(ID3D12GraphicsCommandList4 cl, CpuDescriptorHandle depthSrvCpu) {
        dev.Device.CopyDescriptorsSimple(1, heap.Cpu(SrvDepth), depthSrvCpu,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

        cl.SetDescriptorHeaps(heap.Heap);
        if (state != ResourceStates.UnorderedAccess) {
            cl.ResourceBarrierTransition(pyramid, state, ResourceStates.UnorderedAccess);
            state = ResourceStates.UnorderedAccess;
        }

        cl.SetComputeRootSignature(copyRootSig);
        cl.SetPipelineState(copyPso);
        cl.SetComputeRootDescriptorTable(0, heap.Gpu(SrvDepth));
        cl.SetComputeRootDescriptorTable(1, heap.Gpu(UavBase));
        cl.Dispatch((uint)((width + 7) / 8), (uint)((height + 7) / 8), 1);

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
            cl.SetComputeRootDescriptorTable(1, heap.Gpu(UavBase + mip - 1));
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
