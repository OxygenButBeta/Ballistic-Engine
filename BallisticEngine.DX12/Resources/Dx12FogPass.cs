using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

public sealed class Dx12FogPass : IRenderPass, IDisposable {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.Fog;
    public string Name => "Fog";

    // FAZ -1d-FINAL — when render-graph v2 owns the whole frame (v1 bypassed) it drives Fog itself; the v1
    // graph then SKIPS this pass via RgV2OwnsFog. Door off (and door-on-while-plumbing) => the flag is false
    // => Enabled unchanged. See Dx12FrameContext.RgV2OwnsFog.
    public bool Enabled(Dx12FrameContext ctx) =>
        ((!ctx.Doors.Minimal && ctx.PostFX.VolumetricEnabled) || ctx.Doors.Fog) && !ctx.RgV2OwnsFog;

    // FAZ -1d-FINAL — render-graph v2 entry point (mirrors Dx12TaaPass.RecordV2). v2 imports SceneColor
    // (ReadWrite) + GBuffer (depth read) + ShadowMap, declares the access, then calls this to run the SAME
    // record body (byte-identical to the v1 path). Under v2 the v1 barrier deriver is bypassed (pass skipped
    // in v1) and v2 emits no barrier for the imports (equal states), so the body MUST own the GBuffer depth
    // -> shader-read transition. Force it here so Record() never samples the depth in the wrong state
    // regardless of ctx.BarriersDerived (the body's own guarded `gbuffer.DepthToShaderResource()`).
    public void RecordV2(Dx12FrameContext ctx) {
        ctx.GBuffer.DepthToShaderResource();
        Record(ctx);
    }

    public void Declare(Dx12PassBuilder b) {
        b.Read(b.Resource("GBuffer"));
        b.Read(b.Resource("ShadowMap"));
        b.ReadWrite(b.Resource("SceneColor"));
        b.DeriveBarriers();
        b.Use(Dx12ResourceUsage.GBufferDepthShaderRead);
    }

    const int ShadowMapSize = 2048;
    const int CascadeCount = 4;

    static bool? fogHalfRes;
    static bool FogHalfRes => fogHalfRes ??= Environment.GetEnvironmentVariable("BALLISTIC_DX12_FOG_HALFRES") != "0";

    [StructLayout(LayoutKind.Sequential)]
    struct FogConstants {
        public Matrix4x4 InvViewProj;
        public Matrix4x4 Cascade0, Cascade1, Cascade2, Cascade3;
        public Vector4 CascadeBias;
        public Vector3 CameraPos; public float CascadeCountF;
        public Vector3 SunDirection; public float Density;
        public Vector3 SunColor; public float HeightFalloff;
        public Vector3 SkyAmbient; public float BaseHeight;
        public Vector3 Tint; public float Anisotropy;
        public float Scattering, AmbientScatter, SunGlow, SunGlowSharpness;
        public float StepCount, MaxDistance, ShadowMapTexel, Exposure;
        public Vector3 ShaftTint; public float ShaftIntensity;
        public float ShaftDensity, ShaftDecay, ShaftSharpness, ShaftPad;
        public Vector3 DustDrift; public float DustIntensity;
        public float DustSize, DustSparkle, Time, DustPad;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FogCombineConstants {
        public Matrix4x4 InvProjection;
        public Vector2 HalfTexel; public Vector2 Pad;
    }

    readonly Dx12Device dev;
    ID3D12RootSignature fogRootSig;
    ID3D12PipelineState fogBlendPso;
    ID3D12PipelineState fogMarchPso;
    ID3D12PipelineState fogCombinePso;
    Dx12FrameCb<FogConstants> fogCb;
    Dx12FrameCb<FogCombineConstants> fogCombineCb;
    Dx12DescriptorHeap fogSrvVisible;

    Dx12OffscreenTarget fogHalf;
    Dx12OffscreenTarget fogScene;
    int renderW, renderH;

    public unsafe Dx12FogPass(Dx12Device device, int width, int height) {
        dev = device;
        var cbv0 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var cbv1 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 4, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var sampLinear = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        var sampPoint = new StaticSamplerDescription(ShaderVisibility.Pixel, 1, 0) {
            Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        fogRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv0, cbv1, srvTable },
                new[] { sampLinear, sampPoint })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("VolumetricFog.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "VolumetricFog.hlsl");
        byte[] psMarch = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMarch", "VolumetricFog.hlsl");
        byte[] psCombine = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSCombine", "VolumetricFog.hlsl");

        ID3D12PipelineState MakePso(byte[] ps, BlendDescription blend) =>
            dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
                RootSignature = fogRootSig, VertexShader = vs, PixelShader = ps, InputLayout = null,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
                RasterizerState = RasterizerDescription.CullNone, BlendState = blend,
                DepthStencilState = DepthStencilDescription.None,
                RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat },
                DepthStencilFormat = Format.Unknown, SampleDescription = new SampleDescription(1, 0),
            });

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

        fogBlendPso = MakePso(psMarch, blend);
        fogMarchPso = MakePso(psMarch, BlendDescription.Opaque);
        fogCombinePso = MakePso(psCombine, BlendDescription.Opaque);

        fogCb = new Dx12FrameCb<FogConstants>(dev);
        fogCombineCb = new Dx12FrameCb<FogCombineConstants>(dev);
        fogSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 8, shaderVisible: true, framesInFlight: dev.FramesInFlight);
        renderW = width; renderH = height;
        AllocTargets(width, height);
    }

    public void Resize(int width, int height) { renderW = width; renderH = height; AllocTargets(width, height); }

    void AllocTargets(int width, int height) {
        if (fogHalf  is { IsPlaced: false }) fogHalf.Dispose();
        if (fogScene is { IsPlaced: false }) fogScene.Dispose();
        int hw = Math.Max(1, width / 2), hh = Math.Max(1, height / 2);
        fogHalf  = Dx12RenderTargetPool.AllocOrPool(dev, "fogHalf",  hw, hh,
                       Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: false);
        fogScene = Dx12RenderTargetPool.AllocOrPool(dev, "fogScene", width, height,
                       Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: false);
    }

    public unsafe void Record(Dx12FrameContext ctx) {
        Matrix4x4 viewProj = ctx.ViewProj;
        Vector3 camPos = ctx.CamPos;
        Vector3 sunDir = ctx.LightDir, sunColor = ctx.LightColor;
        Matrix4x4[] cascadeMatrices = ctx.CascadeMatrices;
        Dx12GBuffer gbuffer = ctx.GBuffer;
        Dx12ShadowMap shadowMap = ctx.ShadowMap;
        Dx12OffscreenTarget target = ctx.Target;
        var pf = ctx.PostFX;

        Matrix4x4.Invert(viewProj, out Matrix4x4 invVP);
        Vector3 skyAmbient = new Vector3(2000f, 2200f, 2600f);
        var fc = new FogConstants {
            InvViewProj = Matrix4x4.Transpose(invVP),
            Cascade0 = Matrix4x4.Transpose(cascadeMatrices[0]), Cascade1 = Matrix4x4.Transpose(cascadeMatrices[1]),
            Cascade2 = Matrix4x4.Transpose(cascadeMatrices[2]), Cascade3 = Matrix4x4.Transpose(cascadeMatrices[3]),
            CascadeBias = new Vector4(0.0015f, 0.0020f, 0.0030f, 0.0050f),
            CameraPos = camPos, CascadeCountF = CascadeCount,
            SunDirection = sunDir, Density = pf.VolumetricDensity,
            SunColor = sunColor, HeightFalloff = pf.VolumetricHeightFalloff,
            SkyAmbient = skyAmbient, BaseHeight = pf.VolumetricBaseHeight,
            Tint = pf.VolumetricTint, Anisotropy = pf.VolumetricAnisotropy,
            Scattering = pf.VolumetricScattering * pf.VolumetricIntensity,
            AmbientScatter = pf.VolumetricAmbientScatter * pf.VolumetricIntensity,
            SunGlow = pf.VolumetricSunGlow, SunGlowSharpness = pf.VolumetricSunGlowSharpness,
            StepCount = pf.VolumetricStepCount, MaxDistance = pf.VolumetricMaxDistance,
            ShadowMapTexel = 1f / ShadowMapSize, Exposure = 1.0e-5f,
            ShaftTint = pf.ShaftTint,
            ShaftIntensity = (pf.ShaftsEnabled || ctx.Doors.Shafts) ? pf.ShaftIntensity : 0f,
            ShaftDensity = pf.ShaftDensity, ShaftDecay = pf.ShaftDecay,
            ShaftSharpness = pf.ShaftSharpness, ShaftPad = 0f,
            DustDrift = pf.DustDrift,
            DustIntensity = (pf.DustEnabled || ctx.Doors.Dust) ? pf.DustIntensity : 0f,
            DustSize = pf.DustSize, DustSparkle = pf.DustSparkle,
            Time = ctx.FrameCounter * (1f / 60f), DustPad = 0f,
        };
        fogCb.Write(fc);

        if (!ctx.BarriersDerived) gbuffer.DepthToShaderResource();
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;

        if (!FogHalfRes) {
            dev.Device.CopyDescriptorsSimple(1, fogSrvVisible.Cpu(0), gbuffer.DepthSrvCpu, heapType);
            dev.Device.CopyDescriptorsSimple(1, fogSrvVisible.Cpu(1), shadowMap.SrvCpu, heapType);
            target.RenderColorOnly(cl => {
                cl.SetGraphicsRootSignature(fogRootSig);
                cl.SetPipelineState(fogBlendPso);
                cl.SetDescriptorHeaps(fogSrvVisible.Heap);
                cl.SetGraphicsRootConstantBufferView(0, fogCb.Gpu);
                cl.SetGraphicsRootDescriptorTable(2, fogSrvVisible.Gpu(0));
                cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                cl.DrawInstanced(3, 1, 0, 0);
            });
            return;
        }

        dev.Device.CopyDescriptorsSimple(1, fogSrvVisible.Cpu(0), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, fogSrvVisible.Cpu(1), shadowMap.SrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, fogSrvVisible.Cpu(2), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, fogSrvVisible.Cpu(3), gbuffer.DepthSrvCpu, heapType);
        fogHalf.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(fogRootSig);
            cl.SetPipelineState(fogMarchPso);
            cl.SetDescriptorHeaps(fogSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, fogCb.Gpu);
            cl.SetGraphicsRootDescriptorTable(2, fogSrvVisible.Gpu(0));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });

        Matrix4x4.Invert(ctx.Proj, out Matrix4x4 invProj);
        fogCombineCb.Write(new FogCombineConstants {
            InvProjection = Matrix4x4.Transpose(invProj),
            HalfTexel = new Vector2(1f / fogHalf.Width, 1f / fogHalf.Height),
        });
        fogHalf.ColorToShaderResource();
        target.ColorToShaderResource();
        dev.Device.CopyDescriptorsSimple(1, fogSrvVisible.Cpu(4), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, fogSrvVisible.Cpu(5), shadowMap.SrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, fogSrvVisible.Cpu(6), target.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, fogSrvVisible.Cpu(7), fogHalf.ColorSrvCpu, heapType);
        fogScene.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(fogRootSig);
            cl.SetPipelineState(fogCombinePso);
            cl.SetDescriptorHeaps(fogSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, fogCb.Gpu);
            cl.SetGraphicsRootConstantBufferView(1, fogCombineCb.Gpu);
            cl.SetGraphicsRootDescriptorTable(2, fogSrvVisible.Gpu(4));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });

        fogScene.ColorToShaderResource();
        target.CopyColorFrom(fogScene);
    }

    public void Dispose() {
        if (fogHalf  is { IsPlaced: false }) fogHalf?.Dispose();
        if (fogScene is { IsPlaced: false }) fogScene?.Dispose();
        fogSrvVisible?.Dispose();
        fogCb?.Dispose();
        fogCombineCb?.Dispose();
        fogBlendPso?.Dispose(); fogMarchPso?.Dispose(); fogCombinePso?.Dispose();
        fogRootSig?.Dispose();
    }
}
