using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

public sealed class Dx12AerialPerspectivePass : IRenderPass, IDisposable {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.AerialPerspective;
    public string Name => "AerialPerspective";

    public bool Enabled(Dx12FrameContext ctx) => ctx.Doors.AerialPersp && ProceduralSky.Active is not null;

    public void Declare(Dx12PassBuilder b) {
        b.Read(b.Resource("GBuffer"));
        b.ReadWrite(b.Resource("SceneColor"));
        b.DeriveBarriers();
        b.Use(Dx12ResourceUsage.GBufferDepthShaderRead);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ApConstants {
        public Matrix4x4 InvViewProj;
        public Vector3 CameraPos; public float MaxDistance;
        public float Enabled; public Vector3 PadAp;
    }

    readonly Dx12Device dev;
    readonly Dx12AerialPerspectiveLut lut;
    ID3D12RootSignature apRootSig;
    ID3D12PipelineState apPso;
    Dx12FrameCb<ApConstants> apCb;
    Dx12DescriptorHeap apSrvVisible;

    public unsafe Dx12AerialPerspectivePass(Dx12Device device) {
        dev = device;
        lut = new Dx12AerialPerspectiveLut(device);

        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 2, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var pointSamp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        var linearSamp = new StaticSamplerDescription(ShaderVisibility.Pixel, 1, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        apRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { pointSamp, linearSamp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("AerialPerspective.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "AerialPerspective.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "AerialPerspective.hlsl");

        var blend = BlendDescription.Opaque;
        var rt0 = blend.RenderTarget[0];
        rt0.BlendEnable = true;
        rt0.SourceBlend = Blend.One;
        rt0.DestinationBlend = Blend.SourceAlpha;
        rt0.BlendOperation = BlendOperation.Add;
        rt0.SourceBlendAlpha = Blend.Zero;
        rt0.DestinationBlendAlpha = Blend.Zero;
        rt0.BlendOperationAlpha = BlendOperation.Add;
        blend.RenderTarget[0] = rt0;

        apPso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = apRootSig, VertexShader = vs, PixelShader = ps, InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = blend,
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat },
            DepthStencilFormat = Format.Unknown, SampleDescription = new SampleDescription(1, 0),
        });

        apCb = new Dx12FrameCb<ApConstants>(dev);
        apSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 2, shaderVisible: true, framesInFlight: dev.FramesInFlight);
    }

    public unsafe void Record(Dx12FrameContext ctx) {
        Matrix4x4 viewProj = ctx.ViewProj;
        Vector3 camPos = ctx.CamPos;
        Vector3 lightDir = ctx.LightDir;
        Vector3 sunDir = lightDir.LengthSquared() < 1e-8f ? Vector3.UnitY : Vector3.Normalize(lightDir);
        Vector3 sunRadiance = ctx.LightColor;
        Dx12GBuffer gbuffer = ctx.GBuffer;
        Dx12OffscreenTarget target = ctx.Target;
        var pf = ctx.PostFX;

        Matrix4x4.Invert(viewProj, out Matrix4x4 invVP);
        var pSky = ProceduralSky.Active;
        Vector3 skyTint = sunRadiance * new Vector3(0.10f, 0.16f, 0.32f);

        float intensity = pf.AerialPerspectiveIntensity;
        if (float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_AP_STRENGTH"),
            System.Globalization.CultureInfo.InvariantCulture, out float s)) intensity = s;
        bool apEnabled = pf.AerialPerspectiveEnabled && intensity > 0f;

        lut.Bake(invVP, camPos, sunDir, sunRadiance, skyTint, pSky, pf, apEnabled ? intensity : 0f);

        apCb.Write(new ApConstants {
            InvViewProj = Matrix4x4.Transpose(invVP),
            CameraPos = camPos, MaxDistance = MathF.Max(pf.AerialPerspectiveMaxDistance, 1f),
            Enabled = apEnabled ? 1f : 0f,
        });

        if (!ctx.BarriersDerived) gbuffer.DepthToShaderResource();
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        dev.Device.CopyDescriptorsSimple(1, apSrvVisible.Cpu(0), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, apSrvVisible.Cpu(1), lut.SrvCpu, heapType);

        target.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(apRootSig);
            cl.SetPipelineState(apPso);
            cl.SetDescriptorHeaps(apSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, apCb.Gpu);
            cl.SetGraphicsRootDescriptorTable(1, apSrvVisible.Gpu(0));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
    }

    public void Dispose() {
        lut?.Dispose();
        apSrvVisible?.Dispose();
        apCb?.Dispose();
        apPso?.Dispose();
        apRootSig?.Dispose();
    }
}
