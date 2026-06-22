using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

public sealed class Dx12SkyPass : IRenderPass, IDisposable {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.Sky;
    public string Name => "Sky";

    public bool Enabled(Dx12FrameContext ctx) => ctx.Doors.Sky;

    public void Declare(Dx12PassBuilder b) {
        b.Read(b.Resource("GBuffer"));
        b.ReadWrite(b.Resource("SceneColor"));
        b.DeriveBarriers();
        b.Use(Dx12ResourceUsage.GBufferDepthReadOnly);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SkyboxConstants {
        public Matrix4x4 ViewProjNoTranslate;
        public Matrix4x4 SkyRotation;
        public float Exposure; public Vector3 Pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcSkyConstants {
        public Matrix4x4 ViewProjNoTranslate;
        public Vector3 SunDirection; public float SunAngularRadius;
        public Vector3 SunRadiance; public float SunDiskIntensity;
        public Vector3 GroundAlbedo; public float AirDensity;
        public float Haze, HazeAnisotropy, OzoneDensity, MultiScatter;
        public float Exposure, BakeFace; public Vector2 Pad0;
        public float CloudsEnabled, CloudCoverage, CloudDensity, CloudAltitude;
        public float CloudThickness, CloudScale, CloudDetail, CloudAmbient;
        public Vector3 CloudWindOffset; public float CloudWindAngle;
        public float CirrusCoverage, StarIntensity; public Vector2 Pad1;
    }

    readonly Dx12Device dev;

    ID3D12RootSignature skyRootSig;
    ID3D12PipelineState skyPso;
    Dx12FrameCb<SkyboxConstants> skyCb;
    Dx12DescriptorHeap skySrvVisible;

    ID3D12RootSignature procSkyRootSig;
    ID3D12PipelineState procSkyPso;
    ID3D12PipelineState procSkyBgPso;
    Dx12FrameCb<ProcSkyConstants> procSkyCb;
    Dx12DescriptorHeap procSkyEnvSrvVisible;

    public unsafe Dx12SkyPass(Dx12Device device) {
        dev = device;
        BuildSkybox();
        BuildProcSky();
    }

    unsafe void BuildSkybox() {
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView,
            new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var sampler = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        skyRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { sampler })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("Skybox.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "Skybox.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "Skybox.hlsl");
        var ds = DepthStencilDescription.Default;
        ds.DepthWriteMask = DepthWriteMask.Zero;
        ds.DepthFunc = ComparisonFunction.LessEqual;
        var psoDesc = new GraphicsPipelineStateDescription {
            RootSignature = skyRootSig, VertexShader = vs, PixelShader = ps,
            InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone,
            BlendState = BlendDescription.Opaque, DepthStencilState = ds,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat },
            DepthStencilFormat = Dx12OffscreenTarget.DepthFormat,
            SampleDescription = new SampleDescription(1, 0),
        };
        skyPso = dev.Device.CreateGraphicsPipelineState(psoDesc);

        skyCb = new Dx12FrameCb<SkyboxConstants>(dev);
        skySrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, shaderVisible: true, framesInFlight: dev.FramesInFlight);
    }

    unsafe void BuildProcSky() {
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView,
            new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var sampler = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        procSkyRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { sampler })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("ProceduralSky.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "ProceduralSky.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "ProceduralSky.hlsl");
        byte[] psBg = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSBackground", "ProceduralSky.hlsl");
        var ds = DepthStencilDescription.Default;
        ds.DepthWriteMask = DepthWriteMask.Zero;
        ds.DepthFunc = ComparisonFunction.LessEqual;
        var psoDesc = new GraphicsPipelineStateDescription {
            RootSignature = procSkyRootSig, VertexShader = vs, PixelShader = ps, InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
            DepthStencilState = ds,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat },
            DepthStencilFormat = Dx12OffscreenTarget.DepthFormat,
            SampleDescription = new SampleDescription(1, 0),
        };
        procSkyPso = dev.Device.CreateGraphicsPipelineState(psoDesc);
        psoDesc.PixelShader = psBg;
        procSkyBgPso = dev.Device.CreateGraphicsPipelineState(psoDesc);

        procSkyCb = new Dx12FrameCb<ProcSkyConstants>(dev);
        procSkyEnvSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, shaderVisible: true, framesInFlight: dev.FramesInFlight);
    }

    public unsafe void Record(Dx12FrameContext ctx) {
        Dx12GBuffer gbuffer = ctx.GBuffer;
        Dx12OffscreenTarget target = ctx.Target;

        if (!ctx.BarriersDerived) gbuffer.DepthToReadOnly();
        target.RenderColorWithExternalDepth(gbuffer.DsvHandle, cl => {
            if (ProceduralSky.Active is not null)
                DrawProcSky(cl, ctx);
            else
                DrawSkybox(cl, ctx);
        });
    }

    unsafe void DrawProcSky(ID3D12GraphicsCommandList4 cl, Dx12FrameContext ctx) {
        ProceduralSky sky = ProceduralSky.Active;
        if (sky is null) return;

        Matrix4x4 view = ctx.View, proj = ctx.Proj;
        Matrix4x4 viewNoT = view; viewNoT.M41 = 0; viewNoT.M42 = 0; viewNoT.M43 = 0;
        Vector3 sunDir = ctx.LightDir;
        if (sunDir.LengthSquared() < 1e-8f) sunDir = Vector3.UnitY;
        sunDir = Vector3.Normalize(sunDir);
        float sunAngularRadius = (DirectionalLight.Instance?.AngularDiameter ?? 0.53f) * 0.5f * (MathF.PI / 180f);

        float cloudTime = Dx12SkyCloudParams.CloudTime(sky);
        var sc = new ProcSkyConstants {
            ViewProjNoTranslate = Matrix4x4.Transpose(viewNoT * proj),
            SunDirection = sunDir, SunAngularRadius = MathF.Max(sunAngularRadius, 1e-4f),
            SunRadiance = ctx.LightColor, SunDiskIntensity = MathF.Max(sky.SunDiskIntensity, 0f),
            GroundAlbedo = sky.GroundColor, AirDensity = MathF.Max(sky.AirDensity, 0f),
            Haze = MathF.Max(sky.Haze, 0f), HazeAnisotropy = Math.Clamp(sky.HazeAnisotropy, 0f, 0.99f),
            OzoneDensity = MathF.Max(sky.OzoneDensity, 0f), MultiScatter = MathF.Max(sky.MultipleScattering, 1f),
            Exposure = MathF.Max(sky.Exposure, 0f),
            CloudsEnabled = sky.CloudsEnabled ? 1f : 0f, CloudCoverage = Math.Clamp(sky.CloudCoverage, 0f, 1f),
            CloudDensity = MathF.Max(sky.CloudDensity, 0f), CloudAltitude = Math.Clamp(sky.CloudAltitude, 600f, 20000f),
            CloudThickness = Math.Clamp(sky.CloudThickness, 100f, 20000f), CloudScale = MathF.Max(sky.CloudScale, 0.05f),
            CloudDetail = Math.Clamp(sky.CloudDetail, 0f, 1f), CloudAmbient = MathF.Max(sky.CloudAmbient, 0f),
            CloudWindOffset = Dx12SkyCloudParams.WindOffset(sky, cloudTime),
            CloudWindAngle = Dx12SkyCloudParams.WindRadians(sky),
            CirrusCoverage = Math.Clamp(sky.CirrusCoverage, 0f, 1f), StarIntensity = MathF.Max(sky.StarIntensity, 0f),
        };
        procSkyCb.Write(sc);

        cl.SetGraphicsRootSignature(procSkyRootSig);
        cl.SetGraphicsRootConstantBufferView(0, procSkyCb.Gpu);
        cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        bool forceMarch = Environment.GetEnvironmentVariable("BALLISTIC_DX12_SKY_MARCH") == "1";
        if (!forceMarch && ctx.IblActiveThisFrame && ctx.Ibl is { HasBaked: true }) {
            dev.Device.CopyDescriptorsSimple(1, procSkyEnvSrvVisible.Cpu(0), ctx.Ibl.EnvSrv,
                DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
            cl.SetPipelineState(procSkyBgPso);
            cl.SetDescriptorHeaps(procSkyEnvSrvVisible.Heap);
            cl.SetGraphicsRootDescriptorTable(1, procSkyEnvSrvVisible.Gpu(0));
        } else {
            cl.SetPipelineState(procSkyPso);
        }
        cl.DrawInstanced(36, 1, 0, 0);
    }

    unsafe void DrawSkybox(ID3D12GraphicsCommandList4 cl, Dx12FrameContext ctx) {
        if (Skybox.Active?.Cubemap is not Dx12Texture3D cube || cube.Resource is null)
            return;

        Matrix4x4 view = ctx.View, proj = ctx.Proj;
        Matrix4x4 viewNoT = view; viewNoT.M41 = 0; viewNoT.M42 = 0; viewNoT.M43 = 0;
        Vector3 euler = Skybox.Active.RotationEuler;
        Matrix4x4 rot = Matrix4x4.CreateRotationX(euler.X * (MathF.PI / 180f))
                      * Matrix4x4.CreateRotationY(euler.Y * (MathF.PI / 180f))
                      * Matrix4x4.CreateRotationZ(euler.Z * (MathF.PI / 180f));
        float skyExposure = Skybox.Active.Exposure;

        var sc = new SkyboxConstants {
            ViewProjNoTranslate = Matrix4x4.Transpose(viewNoT * proj),
            SkyRotation = Matrix4x4.Transpose(rot),
            Exposure = skyExposure,
        };
        skyCb.Write(sc);

        dev.Device.CopyDescriptorsSimple(1, skySrvVisible.Cpu(0), cube.SrvCpu,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

        cl.SetGraphicsRootSignature(skyRootSig);
        cl.SetPipelineState(skyPso);
        cl.SetDescriptorHeaps(skySrvVisible.Heap);
        cl.SetGraphicsRootConstantBufferView(0, skyCb.Gpu);
        cl.SetGraphicsRootDescriptorTable(1, skySrvVisible.Gpu(0));
        cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        cl.DrawInstanced(36, 1, 0, 0);
    }

    public void Dispose() {
        procSkyEnvSrvVisible?.Dispose();
        procSkyCb?.Dispose();
        procSkyBgPso?.Dispose();
        procSkyPso?.Dispose();
        procSkyRootSig?.Dispose();
        skySrvVisible?.Dispose();
        skyCb?.Dispose();
        skyPso?.Dispose();
        skyRootSig?.Dispose();
    }
}
