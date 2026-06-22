using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Dxc;

namespace BallisticEngine.DX12;

internal sealed class Dx12VisBufferPass : IDisposable {
    readonly Dx12Device dev;
    readonly Dx12GpuDrivenRenderer gpu;
    ID3D12RootSignature visRootSig, resolveRootSig;
    ID3D12PipelineState visPso, resolvePso;
    ID3D12Resource visTarget;
    ID3D12DescriptorHeap visRtvHeap;
    CpuDescriptorHandle visRtv;
    int visSrvSlot = -1;
    ID3D12Resource resolveCb; unsafe byte* resolveCbMapped; long resolveCbStride;
    int w, h;

    [StructLayout(LayoutKind.Sequential)]
    struct ResolveConstants {
        public Matrix4x4 InvViewProj, ViewProjCur, ViewProjPrev;
        public Vector2 RtSize; public float NormalLodBias; public uint VisIdIndex;
    }

    public bool Available => visPso != null && resolvePso != null;

    public Dx12VisBufferPass(Dx12Device device, Dx12GpuDrivenRenderer gpuDriven) {
        dev = device; gpu = gpuDriven;
        if (!dev.HasMeshShaders) return;
        BuildPipelines();
    }

    unsafe void BuildPipelines() {
        var vp = new List<RootParameter1> { new(new RootConstants(0, 0, 4), ShaderVisibility.All) };
        for (int t = 0; t <= 9; t++) vp.Add(new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1((uint)t, 0), ShaderVisibility.All));
        vp.Add(new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.All));
        vp.Add(new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(2, 0), ShaderVisibility.All));
        var pointS = new StaticSamplerDescription(ShaderVisibility.All, 1, 0) {
            Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1, ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        visRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed, vp.ToArray(), new[] { pointS })));
        string vb = EmbeddedShaderSource.ReadHlsl("VisBuffer.hlsl");
        byte[] asb = Dx12ShaderCompiler.Compile(DxcShaderStage.Amplification, vb, "ASMain", "VisBuffer.hlsl");
        byte[] msb = Dx12ShaderCompiler.Compile(DxcShaderStage.Mesh, vb, "MSMain", "VisBuffer.hlsl");
        byte[] psb = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, vb, "PSMain", "VisBuffer.hlsl");
        var visBlend = new BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
        visBlend.RenderTarget[0] = new RenderTargetBlendDescription {
            BlendEnable = false, LogicOpEnable = false,
            SourceBlend = Blend.One, DestinationBlend = Blend.Zero, BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One, DestinationBlendAlpha = Blend.Zero, BlendOperationAlpha = BlendOperation.Add,
            LogicOp = LogicOp.Noop, RenderTargetWriteMask = ColorWriteEnable.All,
        };
        visPso = Dx12MeshShaderPso.Create(dev.Device, visRootSig, asb, msb, psb,
            RasterizerDescription.CullClockwise, visBlend, DepthStencilDescription.Default,
            new[] { Format.R32G32_UInt }, Dx12GBuffer.DepthFormat);

        var rp = new List<RootParameter1> {
            new(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.All),
        };
        var uavRange = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 5, baseShaderRegister: 0, registerSpace: 0, offsetInDescriptorsFromTableStart: 0);
        rp.Add(new RootParameter1(new RootDescriptorTable1(uavRange), ShaderVisibility.All));
        var wrapS = new StaticSamplerDescription(ShaderVisibility.All, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 16, ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        resolveRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed, rp.ToArray(), new[] { wrapS })));
        string rv = EmbeddedShaderSource.ReadHlsl("VisResolve.hlsl");
        resolvePso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription {
            RootSignature = resolveRootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, rv, "CSMain", "VisResolve.hlsl"),
        });

        resolveCbStride = 256;
        resolveCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(resolveCbStride * dev.FramesInFlight)), ResourceStates.GenericRead);
        resolveCbMapped = resolveCb.Map<byte>(0);
    }

    public void EnsureTarget(int width, int height) {
        if (visTarget != null && w == width && h == height) return;
        w = width; h = height;
        if (visTarget != null) dev.DeferredRelease(visTarget);
        var desc = ResourceDescription.Texture2D(Format.R32G32_UInt, (uint)width, (uint)height, 1, 1);
        desc.Flags = ResourceFlags.AllowRenderTarget;
        visTarget = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            desc, ResourceStates.RenderTarget, new ClearValue(Format.R32G32_UInt, new Vortice.Mathematics.Color4(0, 0, 0, 0)));
        visTarget.Name = "VisBuffer";
        visState = ResourceStates.RenderTarget;
        visRtvHeap ??= dev.Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 1));
        visRtv = visRtvHeap.GetCPUDescriptorHandleForHeapStart();
        dev.Device.CreateRenderTargetView(visTarget, null, visRtv);
        if (visSrvSlot < 0) visSrvSlot = Dx12Backend.BindlessHeap.Allocate();
        dev.Device.CreateShaderResourceView(visTarget, new ShaderResourceViewDescription {
            Format = Format.R32G32_UInt, ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
        }, Dx12Backend.BindlessHeap.Cpu(visSrvSlot));
    }

    ResourceStates visState = ResourceStates.RenderTarget;

    public unsafe int Render(Dx12GBuffer gbuffer, List<IStaticMeshRenderer> renderers,
        Matrix4x4 viewProj, Vector4[] frustumPlanes, Vector3 cameraPos, bool coneCull,
        Matrix4x4 viewProjUnjittered, Matrix4x4 view, float near, float far,
        Matrix4x4 viewProjCurUnjittered, Matrix4x4 viewProjPrevUnjittered, float normalLodBias) {
        if (!Available) return 0;
        EnsureTarget(gbuffer.Width, gbuffer.Height);

        int draws = 0;
        dev.ExecuteSync(cl4 => {
            var cl = cl4.QueryInterfaceOrNull<ID3D12GraphicsCommandList6>();
            if (cl == null) return;
            if (visState != ResourceStates.RenderTarget) {
                cl.ResourceBarrierTransition(visTarget, visState, ResourceStates.RenderTarget);
                visState = ResourceStates.RenderTarget;
            }
            gbuffer.DepthTransitionPublic(cl, ResourceStates.DepthWrite);
            cl.RSSetViewport(0, 0, w, h);
            cl.RSSetScissorRect(w, h);
            cl.ClearRenderTargetView(visRtv, new Vortice.Mathematics.Color4(0, 0, 0, 0));
            Span<CpuDescriptorHandle> rtvs = stackalloc CpuDescriptorHandle[1] { visRtv };
            cl.OMSetRenderTargets(rtvs, gbuffer.DsvHandle);
            int cpuDrawIndex = 0;
            draws = gpu.RenderVis(cl, renderers, visRootSig, visPso, viewProj, frustumPlanes, cameraPos, coneCull,
                viewProjUnjittered, view, near, far, ref cpuDrawIndex);
            cl.Dispose();
        });
        if (draws == 0) return 0;

        long cbOff = (long)dev.FrameSlot * resolveCbStride;
        Matrix4x4.Invert(viewProjUnjittered, out Matrix4x4 invVp);
        *(ResolveConstants*)(resolveCbMapped + cbOff) = new ResolveConstants {
            InvViewProj = Matrix4x4.Transpose(invVp),
            ViewProjCur = Matrix4x4.Transpose(viewProjCurUnjittered),
            ViewProjPrev = Matrix4x4.Transpose(viewProjPrevUnjittered),
            RtSize = new Vector2(w, h), NormalLodBias = normalLodBias, VisIdIndex = (uint)visSrvSlot,
        };
        int uavBase = gpu.ReserveVisResolveUavs();
        for (int i = 0; i < Dx12GBuffer.RtCount; i++)
            gbuffer.CreateColorUav(i, Dx12Backend.BindlessHeap.Cpu(uavBase + i));

        dev.ExecuteSync(cl => {
            if (visState != ResourceStates.NonPixelShaderResource) {
                cl.ResourceBarrierTransition(visTarget, visState, ResourceStates.NonPixelShaderResource);
                visState = ResourceStates.NonPixelShaderResource;
            }
            gbuffer.ColorsToUav(cl);

            cl.SetDescriptorHeaps(Dx12Backend.BindlessHeap.Heap);
            cl.SetComputeRootSignature(resolveRootSig);
            cl.SetPipelineState(resolvePso);
            cl.SetComputeRootConstantBufferView(0, resolveCb.GPUVirtualAddress + (ulong)cbOff);
            cl.SetComputeRootShaderResourceView(1, gpu.VisDrawsAddress);
            cl.SetComputeRootShaderResourceView(2, gpu.MaterialsGpuAddress);
            cl.SetComputeRootDescriptorTable(3, Dx12Backend.BindlessHeap.Gpu(uavBase));
            int gx = (w + 7) / 8, gy = (h + 7) / 8;
            cl.Dispatch((uint)gx, (uint)gy, 1);

            gbuffer.ColorsToShaderRead(cl);
        });
        return draws;
    }

    public void Dispose() {
        visRootSig?.Dispose(); resolveRootSig?.Dispose(); visPso?.Dispose(); resolvePso?.Dispose();
        visTarget?.Dispose(); visRtvHeap?.Dispose(); resolveCb?.Dispose();
    }
}
