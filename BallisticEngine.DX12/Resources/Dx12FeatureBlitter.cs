using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

public sealed class Dx12FeatureBlitter : IDisposable {
    [StructLayout(LayoutKind.Sequential)]
    struct TintConstants { public Vector3 Tint; public float Strength; }

    readonly Dx12Device dev;
    ID3D12RootSignature rootSig;
    ID3D12PipelineState tintPso;
    ID3D12Resource cb;
    unsafe byte* cbMapped;
    Dx12DescriptorHeap srvVisible;
    Dx12OffscreenTarget scratch;

    public unsafe Dx12FeatureBlitter(Dx12Device device, int width, int height) {
        dev = device;
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var samp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        rootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { samp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("SceneColorTint.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "SceneColorTint.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "SceneColorTint.hlsl");
        tintPso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = rootSig, VertexShader = vs, PixelShader = ps, InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat },
            DepthStencilFormat = Format.Unknown, SampleDescription = new SampleDescription(1, 0),
        });

        int cbSize = (Marshal.SizeOf<TintConstants>() + 255) & ~255;
        cb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        cbMapped = cb.Map<byte>(0);
        srvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, shaderVisible: true, framesInFlight: dev.FramesInFlight);
        AllocScratch(width, height);
    }

    public void Resize(int width, int height) => AllocScratch(width, height);

    void AllocScratch(int width, int height) {
        scratch?.Dispose();
        scratch = new Dx12OffscreenTarget(dev, Math.Max(1, width), Math.Max(1, height),
            withDepth: false, colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
    }

    public unsafe void Tint(Dx12OffscreenTarget sceneColor, RenderFeature feature) {
        Vector3 tint = Vector3.One; float strength = 1f;
        if (feature is SceneColorTintFeature t) { tint = t.Tint; strength = t.Strength; }

        if (scratch.Width != sceneColor.Width || scratch.Height != sceneColor.Height)
            AllocScratch(sceneColor.Width, sceneColor.Height);

        *(TintConstants*)cbMapped = new TintConstants { Tint = tint, Strength = strength };

        scratch.CopyColorFrom(sceneColor);
        scratch.ColorToShaderResource();

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(0), scratch.ColorSrvCpu, heapType);
        sceneColor.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(rootSig);
            cl.SetPipelineState(tintPso);
            cl.SetDescriptorHeaps(srvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, cb.GPUVirtualAddress);
            cl.SetGraphicsRootDescriptorTable(1, srvVisible.Gpu(0));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
    }

    public void Dispose() {
        scratch?.Dispose();
        srvVisible?.Dispose();
        cb?.Dispose();
        tintPso?.Dispose();
        rootSig?.Dispose();
    }
}
