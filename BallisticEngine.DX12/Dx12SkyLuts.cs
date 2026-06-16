using System.Numerics;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// Owns the Hillaire 2020 sky-atmosphere LUTs for the DX12 procedural sky, SEPARATE from Dx12IblBaker so the
// sky-atmosphere work doesn't entangle the IBL/GI bake. v1 = the Transmittance LUT (256x64 RGBA16F): the
// atmosphere's transmittance exp(-opticalDepth) toward the top of the atmosphere from any (altitude, view-
// zenith). The sky kernel samples it instead of re-marching the Rayleigh/Mie/ozone optical depth, and the
// renderer samples it CPU-side to redden/dim the directional sun at low elevations (golden hour).
//
// Re-baked only when the atmosphere params change (a hash stamp), so a static scene pays once.
public sealed class Dx12SkyLuts : System.IDisposable {
    const int TransW = 256, TransH = 64;   // Bruneton transmittance LUT resolution

    readonly Dx12Device dev;

    Dx12OffscreenTarget transmittance;     // 256x64 RGBA16F, color-readable (sampled by the sky + CPU readback)
    ID3D12RootSignature transRootSig;      // TransmittanceConstants CBV (b0)
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

    // Re-bake the transmittance LUT if the atmosphere params changed. Cheap (256x64, one FSQ).
    public unsafe void EnsureBaked(float airDensity, float haze, float ozone) {
        int stamp = System.HashCode.Combine(airDensity, haze, ozone);
        if (stamp == paramStamp && HasBaked) return;
        paramStamp = stamp;

        *(TransmittanceConstants*)transCbMapped = new TransmittanceConstants {
            AirDensity = System.MathF.Max(airDensity, 0f), Haze = System.MathF.Max(haze, 0f),
            OzoneDensity = System.MathF.Max(ozone, 0f),
        };

        transmittance.RenderColorOnly(cl => {   // RenderColorOnly transitions to RenderTarget itself
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
