using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

public sealed class Dx12SkyLuts : System.IDisposable {
    const int TransW = 256, TransH = 64;

    readonly Dx12Device dev;

    Dx12OffscreenTarget transmittance;
    ID3D12RootSignature transRootSig;
    ID3D12PipelineState transPso;
    ID3D12Resource transCb;
    unsafe byte* transCbMapped;

    int paramStamp = -1;
    public bool HasBaked { get; private set; }
    public CpuDescriptorHandle TransmittanceSrv => transmittance.ColorSrvCpu;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct TransmittanceConstants { public float AirDensity; public float Haze; public float OzoneDensity; public float Pad; }

    public Dx12SkyLuts(Dx12Device device) {
        dev = device;
        BuildPipelines();
    }

    unsafe void BuildPipelines() {
        transRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] {
                new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.Pixel) })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("SkyTransmittance.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "SkyTransmittance.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "SkyTransmittance.hlsl");
        transPso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = transRootSig, VertexShader = vs, PixelShader = ps, InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat }, DepthStencilFormat = Format.Unknown,
            SampleDescription = new SampleDescription(1, 0),
        });

        transmittance = new Dx12OffscreenTarget(dev, TransW, TransH, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);

        int cbSize = (System.Runtime.InteropServices.Marshal.SizeOf<TransmittanceConstants>() + 255) & ~255;
        transCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        transCbMapped = transCb.Map<byte>(0);
    }

    public unsafe void EnsureBaked(float airDensity, float haze, float ozone) {
        int stamp = System.HashCode.Combine(airDensity, haze, ozone);
        if (stamp == paramStamp && HasBaked) return;
        paramStamp = stamp;

        *(TransmittanceConstants*)transCbMapped = new TransmittanceConstants {
            AirDensity = System.MathF.Max(airDensity, 0f), Haze = System.MathF.Max(haze, 0f),
            OzoneDensity = System.MathF.Max(ozone, 0f),
        };

        transmittance.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(transRootSig);
            cl.SetPipelineState(transPso);
            cl.SetGraphicsRootConstantBufferView(0, transCb.GPUVirtualAddress);
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
        transmittance.ColorToShaderResource();
        HasBaked = true;
    }

    public void Dispose() {
        transmittance?.Dispose();
    }
}
