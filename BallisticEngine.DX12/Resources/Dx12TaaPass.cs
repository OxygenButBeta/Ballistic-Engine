using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

public sealed class Dx12TaaPass : IRenderPass, IDisposable {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.PostProcess;
    public string Name => "TAA";

    public bool Enabled(Dx12FrameContext ctx) => !ctx.FsrActive;

    public void Declare(Dx12PassBuilder b) {
        b.Read(b.Resource("GBuffer"));
        b.ReadWrite(b.Resource("SceneColor"));
        b.DeriveBarriers();
        b.Use(Dx12ResourceUsage.SceneColorShaderRead);
        b.Use(Dx12ResourceUsage.GBufferDepthShaderRead);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TaaConstants { public float Feedback; public float ValidHistory; public Vector2 TexelSize; public float Perceptual; public Vector3 Pad; }

    readonly Dx12Device dev;
    ID3D12RootSignature taaRootSig;
    ID3D12PipelineState taaPso;
    Dx12FrameCb<TaaConstants> taaCb;
    Dx12OffscreenTarget taaHistoryA, taaHistoryB;
    Dx12OffscreenTarget taaResolved;
    Dx12DescriptorHeap taaSrvVisible;
    bool taaWriteB;
    bool taaHistoryValid;

    public unsafe Dx12TaaPass(Dx12Device device, int width, int height) {
        dev = device;
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 4, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var samp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        taaRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { samp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("Taa.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "Taa.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "Taa.hlsl");
        taaPso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = taaRootSig, VertexShader = vs, PixelShader = ps, InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat }, DepthStencilFormat = Format.Unknown,
            SampleDescription = new SampleDescription(1, 0),
        });

        taaCb = new Dx12FrameCb<TaaConstants>(dev);
        taaSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 4, shaderVisible: true, framesInFlight: dev.FramesInFlight);
        AllocTargets(width, height);
    }

    public void Resize(int width, int height) => AllocTargets(width, height);

    void AllocTargets(int width, int height) {
        taaHistoryA?.Dispose(); taaHistoryB?.Dispose(); taaResolved?.Dispose();
        taaHistoryA = new Dx12OffscreenTarget(dev, width, height, withDepth: false, colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
        taaHistoryB = new Dx12OffscreenTarget(dev, width, height, withDepth: false, colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
        taaResolved = new Dx12OffscreenTarget(dev, width, height, withDepth: false, colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
        taaHistoryValid = false;
    }

    public unsafe void Record(Dx12FrameContext ctx) {
        if (!ctx.TaaActive) { taaHistoryValid = false; return; }

        Dx12OffscreenTarget target = ctx.Target;
        Dx12GBuffer gbuffer = ctx.GBuffer;
        int targetW = ctx.TargetW, targetH = ctx.TargetH;
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Dx12OffscreenTarget history = taaWriteB ? taaHistoryA : taaHistoryB;
        Dx12OffscreenTarget writeHist = taaWriteB ? taaHistoryB : taaHistoryA;

        bool perceptual = Environment.GetEnvironmentVariable("BALLISTIC_DX12_TAA_PERCEPTUAL") == "1";
        taaCb.Write(new TaaConstants {
            Feedback = ctx.PostFX.TaaFeedback, ValidHistory = taaHistoryValid ? 1f : 0f,
            TexelSize = new Vector2(1f / targetW, 1f / targetH),
            Perceptual = perceptual ? 1f : 0f,
        });

        if (!ctx.BarriersDerived) target.ColorToShaderResource();
        history.ColorToShaderResource();
        if (!ctx.BarriersDerived) gbuffer.DepthToShaderResource();
        taaSrvVisible.Reset();
        int b = taaSrvVisible.AllocateRange(4);
        dev.Device.CopyDescriptorsSimple(1, taaSrvVisible.Cpu(b + 0), target.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, taaSrvVisible.Cpu(b + 1), history.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, taaSrvVisible.Cpu(b + 2), gbuffer.ColorSrvCpu(Dx12GBuffer.MotionRtIndex), heapType);
        dev.Device.CopyDescriptorsSimple(1, taaSrvVisible.Cpu(b + 3), gbuffer.DepthSrvCpu, heapType);
        writeHist.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(taaRootSig); cl.SetPipelineState(taaPso);
            cl.SetDescriptorHeaps(taaSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, taaCb.Gpu);
            cl.SetGraphicsRootDescriptorTable(1, taaSrvVisible.Gpu(b));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
        writeHist.ColorToShaderResource();
        target.CopyColorFrom(writeHist);

        taaWriteB = !taaWriteB;
        taaHistoryValid = true;
    }

    public void Dispose() {
        taaHistoryA?.Dispose(); taaHistoryB?.Dispose(); taaResolved?.Dispose();
        taaSrvVisible?.Dispose(); taaCb?.Dispose();
        taaPso?.Dispose(); taaRootSig?.Dispose();
    }
}
