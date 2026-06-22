using Vortice.Direct3D12;
using Vortice.Dxc;
using GLVector3 = System.Numerics.Vector3;

namespace BallisticEngine.DX12;

public sealed class Dx12InstanceCuller : IDisposable {
    const int MaxInstances = 65536;

    readonly Dx12Device dev;

    ID3D12RootSignature cullRootSig;
    ID3D12PipelineState cullPso;
    ID3D12RootSignature drawRootSig;
    ID3D12PipelineState drawPso;
    ID3D12CommandSignature cmdSig;

    int capacity;
    ID3D12Resource instances;     unsafe byte* instancesMapped;
    ID3D12Resource visibleIndices;
    ID3D12Resource drawArgs;
    ID3D12Resource drawArgsSeed; unsafe byte* drawArgsSeedMapped;
    ResourceStates drawArgsState = ResourceStates.IndirectArgument;
    ID3D12Resource cullParamCb;   unsafe byte* cullParamMapped;
    ID3D12Resource viewCb;        unsafe byte* viewCbMapped;
    long instancesFrameStride, drawArgsFrameStride, cullParamFrameStride, viewCbFrameStride;
    int instanceStride, cullParamSlotSize, viewCbSlotSize;
    int callsThisFrame;
    const int MaxCallsPerFrame = 256;

    public long LastVisibleUpperBound;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct InstanceData { public Matrix4x4 Model; public Vector4 AabbMin; public Vector4 AabbMax; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct InstCullParams {
        public Vector4 P0, P1, P2, P3, P4, P5;
        public uint InstanceCount, HizEnabled, HizIndex, Pad0;
        public Matrix4x4 ViewProj, View;
        public Vector4 HizParams, HizFar;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct DrawArgs { public uint IndexCount, InstanceCount, StartIndex, BaseVertex, StartInstance; }

    public Dx12InstanceCuller(Dx12Device device) {
        dev = device;
        instanceStride = System.Runtime.InteropServices.Marshal.SizeOf<InstanceData>();
        cullParamSlotSize = (System.Runtime.InteropServices.Marshal.SizeOf<InstCullParams>() + 255) & ~255;
        viewCbSlotSize = 256;
        BuildPipelines();
        EnsureCapacity(4096);
        AllocateFixedBuffers();
    }

    unsafe void BuildPipelines() {
        var cullParams = new[] {
            new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All),
            new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All),
            new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(0, 0), ShaderVisibility.All),
            new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(1, 0), ShaderVisibility.All),
        };
        var pointClamp = new StaticSamplerDescription(ShaderVisibility.All, 0, 0) {
            Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        cullRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(
                RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
                cullParams, new[] { pointClamp })));
        cullPso = dev.CreateComputePso(new ComputePipelineStateDescription {
            RootSignature = cullRootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute,
                EmbeddedShaderSource.ReadHlsl("InstanceCull.hlsl"), "CSMain", "InstanceCull.hlsl"),
        }, "InstanceCull.Cull");

        var drawParams = new[] {
            new RootParameter1(new RootConstants(0, 0, 1), ShaderVisibility.Vertex), new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.All), new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(2, 0), ShaderVisibility.Vertex), new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.Vertex), new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.Vertex), new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(2, 0), ShaderVisibility.Pixel),
        };
        var wrap = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 16,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        drawRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(
                RootSignatureFlags.AllowInputAssemblerInputLayout |
                RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
                drawParams, new[] { wrap })));

        string drawHlsl = EmbeddedShaderSource.ReadHlsl("InstanceGBuffer.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, drawHlsl, "VSMain", "InstanceGBuffer.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, drawHlsl, "PSMain", "InstanceGBuffer.hlsl");
        var layout = new InputLayoutDescription(
            new InputElementDescription("POSITION", 0, Vortice.DXGI.Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Vortice.DXGI.Format.R32G32B32_Float, 0, 1),
            new InputElementDescription("TEXCOORD", 0, Vortice.DXGI.Format.R32G32_Float, 0, 2),
            new InputElementDescription("TANGENT", 0, Vortice.DXGI.Format.R32G32B32A32_Float, 0, 3));
        drawPso = dev.CreateGraphicsPso(new GraphicsPipelineStateDescription {
            RootSignature = drawRootSig, VertexShader = vs, PixelShader = ps, InputLayout = layout,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullClockwise,
            BlendState = BlendDescription.Opaque, DepthStencilState = DepthStencilDescription.Default,
            RenderTargetFormats = Dx12GBuffer.ColorFormats,
            DepthStencilFormat = Dx12GBuffer.DepthFormat, SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
        }, "InstanceCull.Draw");

        var argDraw = new IndirectArgumentDescription { Type = IndirectArgumentType.DrawIndexed };
        cmdSig = dev.Device.CreateCommandSignature<ID3D12CommandSignature>(
            new CommandSignatureDescription(
                System.Runtime.InteropServices.Marshal.SizeOf<DrawArgs>(), new[] { argDraw }), null);
    }

    unsafe void AllocateFixedBuffers() {
        int argStride = System.Runtime.InteropServices.Marshal.SizeOf<DrawArgs>();
        drawArgsFrameStride = (long)argStride * MaxCallsPerFrame;
        ulong argTotal = (ulong)(drawArgsFrameStride * dev.FramesInFlight);
        drawArgs = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(argTotal, ResourceFlags.AllowUnorderedAccess), ResourceStates.IndirectArgument);
        drawArgs.Name = "InstanceCull.DrawArgs";
        drawArgsState = ResourceStates.IndirectArgument;
        drawArgsSeed = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(argTotal), ResourceStates.GenericRead);
        drawArgsSeedMapped = drawArgsSeed.Map<byte>(0);

        cullParamFrameStride = (long)cullParamSlotSize * MaxCallsPerFrame;
        cullParamCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(cullParamFrameStride * dev.FramesInFlight)), ResourceStates.GenericRead);
        cullParamMapped = cullParamCb.Map<byte>(0);

        viewCbFrameStride = (long)viewCbSlotSize * MaxCallsPerFrame;
        viewCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(viewCbFrameStride * dev.FramesInFlight)), ResourceStates.GenericRead);
        viewCbMapped = viewCb.Map<byte>(0);
    }

    unsafe void EnsureCapacity(int count) {
        if (instances != null && count <= capacity) return;
        int newCap = Math.Max(count, capacity == 0 ? 4096 : capacity * 2);
        if (instances != null) { instances.Unmap(0); dev.DeferredRelease(instances); }
        if (visibleIndices != null) dev.DeferredRelease(visibleIndices);
        capacity = newCap;
        instancesFrameStride = (long)instanceStride * capacity;
        instances = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(instancesFrameStride * dev.FramesInFlight)), ResourceStates.GenericRead);
        instances.Name = "InstanceCull.Instances";
        instancesMapped = instances.Map<byte>(0);
        visibleIndices = dev.CreateUavBuffer<uint>(new uint[capacity], ResourceStates.NonPixelShaderResource);
        visibleIndices.Name = "InstanceCull.VisibleIndices";
    }

    public void BeginFrame() { callsThisFrame = 0; LastVisibleUpperBound = 0; }

    public unsafe bool RenderInstanced(ID3D12GraphicsCommandList4 cl, Mesh mesh, int subMeshIndex, int materialId,
        Matrix4x4[] transforms, Matrix4x4 viewProj, Vector4[] frustumPlanes,
        Matrix4x4 viewProjUnjittered, Matrix4x4 view, float near, float far,
        ulong motionCbGpu, ulong materialsGpu, int hizBindlessIndex, bool hizOn) {
        int n = transforms?.Length ?? 0;
        if (n <= 0 || materialId < 0 || callsThisFrame >= MaxCallsPerFrame) return false;
        var vb = mesh.VertexBuffer as Dx12Buffer<GLVector3>;
        var ib = mesh.IndexBuffer as Dx12IndexBuffer;
        var nb = mesh.NormalBuffer as Dx12Buffer<GLVector3>;
        var ub = mesh.UvBuffer as Dx12Buffer<Vector2>;
        var tb = mesh.TangentBuffer as Dx12Buffer<Vector4>;
        if (vb?.Resource is null || ib?.Resource is null || nb?.Resource is null ||
            ub?.Resource is null || tb?.Resource is null) return false;

        int sub = subMeshIndex >= 0 ? subMeshIndex : 0;
        if ((uint)sub >= (uint)mesh.SubMeshes.Length) return false;
        SubMeshData sm = mesh.SubMeshes[sub];
        if (sm.IndexCount <= 0) return false;
        mesh.GetSubMeshBounds(sub, out GLVector3 lmin, out GLVector3 lmax);

        EnsureCapacity(n);
        int call = callsThisFrame++;
        long instSlot = (long)dev.FrameSlot * instancesFrameStride;
        long argSlot = (long)dev.FrameSlot * drawArgsFrameStride + (long)call * System.Runtime.InteropServices.Marshal.SizeOf<DrawArgs>();
        long cpSlot = (long)dev.FrameSlot * cullParamFrameStride + (long)call * cullParamSlotSize;
        long vwSlot = (long)dev.FrameSlot * viewCbFrameStride + (long)call * viewCbSlotSize;

        var aMin = new Vector4(lmin.X, lmin.Y, lmin.Z, 0);
        var aMax = new Vector4(lmax.X, lmax.Y, lmax.Z, 0);
        for (int i = 0; i < n; i++) {
            *(InstanceData*)(instancesMapped + instSlot + (long)i * instanceStride) = new InstanceData {
                Model = Matrix4x4.Transpose(transforms[i]), AabbMin = aMin, AabbMax = aMax,
            };
        }
        LastVisibleUpperBound += n;

        *(DrawArgs*)(drawArgsSeedMapped + argSlot) = new DrawArgs {
            IndexCount = (uint)sm.IndexCount, InstanceCount = 0,
            StartIndex = (uint)sm.IndexStart, BaseVertex = 0, StartInstance = 0,
        };

        bool hizEnabled = hizOn && hizBindlessIndex >= 0;
        *(InstCullParams*)(cullParamMapped + cpSlot) = new InstCullParams {
            P0 = frustumPlanes[0], P1 = frustumPlanes[1], P2 = frustumPlanes[2],
            P3 = frustumPlanes[3], P4 = frustumPlanes[4], P5 = frustumPlanes[5],
            InstanceCount = (uint)n, HizEnabled = hizEnabled ? 1u : 0u,
            HizIndex = (uint)Math.Max(hizBindlessIndex, 0), Pad0 = 0,
            ViewProj = Matrix4x4.Transpose(viewProjUnjittered), View = Matrix4x4.Transpose(view),
            HizParams = new Vector4(hizW, hizH, hizMips, near), HizFar = new Vector4(far, 0, 0, 0),
        };

        *(Matrix4x4*)(viewCbMapped + vwSlot) = Matrix4x4.Transpose(viewProj);

        ulong instGpu = instances.GPUVirtualAddress + (ulong)instSlot;

        if (drawArgsState != ResourceStates.CopyDest) {
            cl.ResourceBarrierTransition(drawArgs, drawArgsState, ResourceStates.CopyDest);
            drawArgsState = ResourceStates.CopyDest;
        }
        cl.CopyBufferRegion(drawArgs, (ulong)argSlot, drawArgsSeed, (ulong)argSlot,
            (ulong)System.Runtime.InteropServices.Marshal.SizeOf<DrawArgs>());
        cl.ResourceBarrierTransition(drawArgs, ResourceStates.CopyDest, ResourceStates.UnorderedAccess);
        drawArgsState = ResourceStates.UnorderedAccess;

        cl.ResourceBarrierTransition(visibleIndices, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
        cl.SetDescriptorHeaps(Dx12Backend.BindlessHeap.Heap);
        cl.SetComputeRootSignature(cullRootSig);
        cl.SetPipelineState(cullPso);
        cl.SetComputeRootConstantBufferView(0, cullParamCb.GPUVirtualAddress + (ulong)cpSlot);
        cl.SetComputeRootShaderResourceView(1, instGpu);
        cl.SetComputeRootUnorderedAccessView(2, visibleIndices.GPUVirtualAddress);
        cl.SetComputeRootUnorderedAccessView(3, drawArgs.GPUVirtualAddress + (ulong)argSlot);
        cl.Dispatch((uint)((n + 63) / 64), 1, 1);

        cl.ResourceBarrierTransition(visibleIndices, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
        cl.ResourceBarrierTransition(drawArgs, ResourceStates.UnorderedAccess, ResourceStates.IndirectArgument);
        drawArgsState = ResourceStates.IndirectArgument;

        cl.SetDescriptorHeaps(Dx12Backend.BindlessHeap.Heap);
        cl.SetGraphicsRootSignature(drawRootSig);
        cl.SetPipelineState(drawPso);
        cl.SetGraphicsRoot32BitConstant(0, (uint)materialId, 0);
        cl.SetGraphicsRootConstantBufferView(1, motionCbGpu);
        cl.SetGraphicsRootConstantBufferView(2, viewCb.GPUVirtualAddress + (ulong)vwSlot);
        cl.SetGraphicsRootShaderResourceView(3, instGpu);
        cl.SetGraphicsRootShaderResourceView(4, visibleIndices.GPUVirtualAddress);
        cl.SetGraphicsRootShaderResourceView(5, materialsGpu);
        cl.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
        Span<VertexBufferView> vbViews = stackalloc VertexBufferView[4];
        vbViews[0] = new VertexBufferView(vb.GpuAddress, (uint)vb.ByteSize, (uint)vb.Stride);
        vbViews[1] = new VertexBufferView(nb.GpuAddress, (uint)nb.ByteSize, (uint)nb.Stride);
        vbViews[2] = new VertexBufferView(ub.GpuAddress, (uint)ub.ByteSize, (uint)ub.Stride);
        vbViews[3] = new VertexBufferView(tb.GpuAddress, (uint)tb.ByteSize, (uint)tb.Stride);
        cl.IASetVertexBuffers(0, vbViews);
        cl.IASetIndexBuffer(new IndexBufferView(ib.GpuAddress, (uint)ib.ByteSize, Vortice.DXGI.Format.R32_UInt));
        cl.ExecuteIndirect(cmdSig, 1, drawArgs, (ulong)argSlot, null, 0);
        return true;
    }

    float hizW, hizH, hizMips;
    public void SetHizDims(int width, int height, int mips) { hizW = width; hizH = height; hizMips = mips; }

    public unsafe void Dispose() {
        cullRootSig?.Dispose(); cullPso?.Dispose();
        drawRootSig?.Dispose(); drawPso?.Dispose(); cmdSig?.Dispose();
        if (instances != null) instances.Unmap(0);
        instances?.Dispose(); visibleIndices?.Dispose();
        drawArgs?.Dispose(); drawArgsSeed?.Dispose(); cullParamCb?.Dispose(); viewCb?.Dispose();
    }
}
