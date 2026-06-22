using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

public sealed class Dx12TriangleTest : IDisposable {
    readonly ID3D12RootSignature rootSig;
    readonly ID3D12PipelineState pso;

    public Dx12TriangleTest(Dx12Device dev) {
        string hlsl = EmbeddedShaderSource.ReadHlsl("Triangle.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "Triangle.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "Triangle.hlsl");

        var rsDesc = new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout);
        rootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(rsDesc));

        var psoDesc = new GraphicsPipelineStateDescription {
            RootSignature = rootSig,
            VertexShader = vs,
            PixelShader = ps,
            InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone,
            BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.ColorFormat },
            DepthStencilFormat = Format.Unknown,
            SampleDescription = new SampleDescription(1, 0),
        };
        pso = dev.Device.CreateGraphicsPipelineState(psoDesc);
    }

    public void Render(Dx12OffscreenTarget target) {
        target.RenderInto(cl => {
            cl.SetPipelineState(pso);
            cl.SetGraphicsRootSignature(rootSig);
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
    }

    public void Dispose() {
        pso.Dispose();
        rootSig.Dispose();
    }
}
