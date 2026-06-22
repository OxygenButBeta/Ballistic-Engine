using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

public sealed class Dx12DepthOfFieldPass : IRenderPass, IDisposable {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.PostProcess;
    public string Name => "DepthOfField";

    // FAZ -1d — when render-graph v2 owns Depth of Field (BALLISTIC_DX12_RG=1) the v1 graph SKIPS this
    // pass; v2 drives Record() itself. Door off (default) => RgV2OwnsDof is false => Enabled unchanged.
    public bool Enabled(Dx12FrameContext ctx) =>
        ctx.PostFX.DofEnabled && !ctx.DeterministicCapture && !ctx.RgV2OwnsDof;

    // FAZ -1d — render-graph v2 entry point (mirrors Dx12TaaPass.RecordV2). v2 imports SceneColor
    // (ReadWrite) + GBuffer (depth read), declares the access, then calls this to run the SAME record body
    // (byte-identical to the v1 path). The v1 graph normally derives the GBuffer depth and SceneColor ->
    // shader-read transitions when ctx.BarriersDerived is on (the body then skips its own — see the
    // `if (!ctx.BarriersDerived)` guard in Record). Under v2 the v1 deriver is bypassed (the pass is skipped
    // in v1) AND v2 emits no barrier for the imports (by design — equal states), so the body MUST own those
    // transitions. Force them here so Record() never reads SceneColor or the GBuffer depth in the wrong
    // state regardless of ctx.BarriersDerived.
    public void RecordV2(Dx12FrameContext ctx) {
        ctx.SceneColor.ColorToShaderResource();
        ctx.GBuffer.ToShaderResource();
        Record(ctx);
    }

    public void Declare(Dx12PassBuilder b) {
        b.Read(b.Resource("GBuffer"));
        b.ReadWrite(b.Resource("SceneColor"));
        b.DeriveBarriers();
        b.Use(Dx12ResourceUsage.GBufferDepthShaderRead);
        b.Use(Dx12ResourceUsage.SceneColorShaderRead);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DofConstants {
        public Matrix4x4 InvProjection;
        public Vector2 TexelSize;
        public Vector2 FullTexelSize;
        public float FocusDistance;
        public float FocalLength;
        public float Aperture;
        public float MaxCoc;
        public float Near, Far;
        public Vector2 Pad;
    }

    readonly Dx12Device dev;
    ID3D12RootSignature rootSig;
    ID3D12PipelineState cocPso, dilatePso, gatherPso, compositePso;
    Dx12OffscreenTarget dofHalf;
    Dx12OffscreenTarget dofNear;
    Dx12OffscreenTarget dofFar;
    Dx12OffscreenTarget dofResult;
    ID3D12Resource cb;
    unsafe byte* cbMapped;
    int cbStride;
    Dx12DescriptorHeap srvVisible;
    int renderW, renderH;

    public unsafe Dx12DepthOfFieldPass(Dx12Device device, int width, int height) {
        dev = device;
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 3, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var linear = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        var point = new StaticSamplerDescription(ShaderVisibility.Pixel, 1, 0) {
            Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        rootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { linear, point })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("DepthOfField.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "DepthOfField.hlsl");
        ID3D12PipelineState MakePso(string entry, Format fmt) => dev.Device.CreateGraphicsPipelineState(
            new GraphicsPipelineStateDescription {
                RootSignature = rootSig, VertexShader = vs,
                PixelShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, entry, "DepthOfField.hlsl"),
                InputLayout = null, PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
                RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
                DepthStencilState = DepthStencilDescription.None,
                RenderTargetFormats = new[] { fmt }, DepthStencilFormat = Format.Unknown,
                SampleDescription = new SampleDescription(1, 0),
            });
        cocPso       = MakePso("PSCoc",       Dx12OffscreenTarget.HdrFormat);
        dilatePso    = MakePso("PSDilate",    Dx12OffscreenTarget.HdrFormat);
        gatherPso    = MakePso("PSGather",    Dx12OffscreenTarget.HdrFormat);
        compositePso = MakePso("PSComposite", Dx12OffscreenTarget.HdrFormat);

        cbStride = (Marshal.SizeOf<DofConstants>() + 255) & ~255;
        cb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(cbStride * 4)), ResourceStates.GenericRead);
        cbMapped = cb.Map<byte>(0);
        srvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 12, shaderVisible: true, framesInFlight: dev.FramesInFlight);
        renderW = width; renderH = height;
        AllocTargets(width, height);
    }

    public void Resize(int width, int height) { renderW = width; renderH = height; AllocTargets(width, height); }

    void AllocTargets(int width, int height) {
        if (dofHalf   is { IsPlaced: false }) dofHalf.Dispose();
        if (dofNear   is { IsPlaced: false }) dofNear.Dispose();
        if (dofFar    is { IsPlaced: false }) dofFar.Dispose();
        if (dofResult is { IsPlaced: false }) dofResult.Dispose();
        int hw = System.Math.Max(1, width / 2), hh = System.Math.Max(1, height / 2);
        dofHalf   = Dx12RenderTargetPool.AllocOrPool(dev, "dofHalf",   hw, hh, Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: false);
        dofNear   = Dx12RenderTargetPool.AllocOrPool(dev, "dofNear",   hw, hh, Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: false);
        dofFar    = Dx12RenderTargetPool.AllocOrPool(dev, "dofFar",    hw, hh, Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: false);
        dofResult = Dx12RenderTargetPool.AllocOrPool(dev, "dofResult", width, height, Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: false);
    }

    public unsafe void Record(Dx12FrameContext ctx) {
        var pf = ctx.PostFX;
        Dx12RenderTargetPool.PoolBarrier(ctx.Dev, "dofHalf", "dofNear", "dofFar", "dofResult");
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Dx12GBuffer gbuffer = ctx.GBuffer;
        Dx12OffscreenTarget scene = ctx.SceneColor;

        Matrix4x4.Invert(ctx.Proj, out Matrix4x4 invProj);
        float m33 = ctx.Proj.M33, m43 = ctx.Proj.M43;
        float near = m43 / m33;
        float far  = m43 / (m33 - 1f);

        if (!ctx.BarriersDerived) { gbuffer.DepthToShaderResource(); scene.ColorToShaderResource(); }

        var baseC = new DofConstants {
            InvProjection = Matrix4x4.Transpose(invProj),
            TexelSize = new Vector2(1f / dofHalf.Width, 1f / dofHalf.Height),
            FullTexelSize = new Vector2(1f / dofResult.Width, 1f / dofResult.Height),
            FocusDistance = MathF.Max(pf.DofFocusDistance, 1e-3f),
            FocalLength = MathF.Max(pf.DofFocalLength, 1e-4f),
            Aperture = MathF.Max(pf.DofAperture, 0.1f),
            MaxCoc = MathF.Max(pf.DofMaxCoc, 0f),
            Near = near, Far = far,
        };

        void WriteCb(int slot, DofConstants c) => *(DofConstants*)(cbMapped + slot * cbStride) = c;

        void Draw(ID3D12PipelineState pso, Dx12OffscreenTarget dst, int slot) {
            dst.RenderColorOnly(cl => {
                cl.SetGraphicsRootSignature(rootSig); cl.SetPipelineState(pso);
                cl.SetDescriptorHeaps(srvVisible.Heap);
                cl.SetGraphicsRootConstantBufferView(0, cb.GPUVirtualAddress + (ulong)(slot * cbStride));
                cl.SetGraphicsRootDescriptorTable(1, srvVisible.Gpu(slot * 3));
                cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                cl.DrawInstanced(3, 1, 0, 0);
            });
        }

        WriteCb(0, baseC);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(0), scene.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(1), scene.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(2), gbuffer.DepthSrvCpu, heapType);
        Draw(cocPso, dofHalf, 0);

        WriteCb(1, baseC);
        dofHalf.ColorToShaderResource();
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(3), dofHalf.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(4), dofHalf.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(5), dofHalf.ColorSrvCpu, heapType);
        Draw(dilatePso, dofNear, 1);

        WriteCb(2, baseC);
        dofNear.ColorToShaderResource();
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(6), dofHalf.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(7), dofNear.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(8), dofHalf.ColorSrvCpu, heapType);
        Draw(gatherPso, dofFar, 2);

        WriteCb(3, baseC);
        dofFar.ColorToShaderResource();
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(9), scene.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(10), dofFar.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(11), dofNear.ColorSrvCpu, heapType);
        Draw(compositePso, dofResult, 3);

        dofResult.ColorToShaderResource();
        scene.CopyColorFrom(dofResult);
        scene.ColorToShaderResource();
    }

    public void Dispose() {
        if (dofHalf   is { IsPlaced: false }) dofHalf.Dispose();
        if (dofNear   is { IsPlaced: false }) dofNear.Dispose();
        if (dofFar    is { IsPlaced: false }) dofFar.Dispose();
        if (dofResult is { IsPlaced: false }) dofResult.Dispose();
        srvVisible?.Dispose();
        cb?.Dispose();
        cocPso?.Dispose(); dilatePso?.Dispose(); gatherPso?.Dispose(); compositePso?.Dispose();
        rootSig?.Dispose();
    }
}
