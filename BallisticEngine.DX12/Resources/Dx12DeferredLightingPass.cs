using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

public sealed class Dx12DeferredLightingPass : IRenderPass, IDisposable {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.OpaqueLighting;
    public string Name => "Deferred";

    public bool Enabled(Dx12FrameContext ctx) => true;

    public void Declare(Dx12PassBuilder b) {
        b.Read(b.Resource("GBuffer"));
        b.Read(b.Resource("ShadowMap"));
        b.Read(b.Resource("RtShadowMask"));
        b.Write(b.Resource("SceneColor"));
        b.DeriveBarriers();
        b.Use(Dx12ResourceUsage.GBufferShaderRead);
    }

    const float CameraNear = 0.1f, CameraFar = 1000f;

    [StructLayout(LayoutKind.Sequential)]
    struct LightConstants {
        public Matrix4x4 InvViewProj;
        public Matrix4x4 View;
        public Vector3 LightDir; public float Pad0;
        public Vector3 LightColor; public float Pad1;
        public Vector3 Ambient; public float Pad2;
        public Vector3 CameraPos; public float UseIBL;
        public float PrefilterMaxMip;
        public float PunctualCount;
        public Vector2 ScreenSize;
        public Vector2 ClusterNearFar;
        public float UseRtShadows; public float SpecClamp;
        public float SpecAaStrength; public float UseSsao;
        public float UseIBLDiffuse; public float UseIBLSpecular;
        public float UseCapsuleShadows; public float CapPad0, CapPad1, CapPad2;

        public Matrix4x4 ViewProjFwd;

        public float UseVsm; public float VsmLevels; public float VsmTexel; public float VsmLevel0Extent;
        public Vector3 VsmCamPos; public float MsBrdfEnabled;
    }

    [StructLayout(LayoutKind.Sequential)]
    unsafe struct VsmConstants {
        public fixed float Matrices[16 * 16];
    }

    readonly Dx12Device dev;
    ID3D12RootSignature deferredRootSig;
    ID3D12PipelineState deferredPso;
    Dx12FrameCb<LightConstants> deferredCb;
    Dx12FrameCb<VsmConstants> vsmCb;
    Dx12DescriptorHeap deferredSrvVisible;
    Dx12LtcTables ltcTables;

    public unsafe Dx12DeferredLightingPass(Dx12Device device) {
        dev = device;
        var lightCbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.Pixel);
        var frameCbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.Pixel);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 18, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var vsmCbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(2, 0), ShaderVisibility.Pixel);
        var samp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        deferredRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None,
                new[] { lightCbv, frameCbv, srvTable, vsmCbv }, new[] { samp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("DeferredLighting.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "DeferredLighting.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "DeferredLighting.hlsl");
        deferredPso = dev.CreateGraphicsPso(new GraphicsPipelineStateDescription {
            RootSignature = deferredRootSig, VertexShader = vs, PixelShader = ps, InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat },
            DepthStencilFormat = Format.Unknown, SampleDescription = new SampleDescription(1, 0),
        }, "Deferred");

        deferredCb = new Dx12FrameCb<LightConstants>(dev);
        vsmCb = new Dx12FrameCb<VsmConstants>(dev);
        deferredSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 18, shaderVisible: true, framesInFlight: dev.FramesInFlight);
        ltcTables = new Dx12LtcTables(dev);
    }

    public unsafe void Record(Dx12FrameContext ctx) {
        var gbuffer = ctx.GBuffer;
        var ibl = ctx.Ibl;
        var shadowMap = ctx.ShadowMap;
        var clusteredLights = ctx.ClusteredLights;
        var rtShadowMask = ctx.RtShadowMask;
        var target = ctx.Target;
        int targetW = ctx.TargetW, targetH = ctx.TargetH;

        if (!ctx.BarriersDerived) gbuffer.ToShaderResource();

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Matrix4x4.Invert(ctx.ViewProj, out Matrix4x4 invVP);
        float specClampValue = 8000f;
        if (float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_SPEC_CLAMP"),
            System.Globalization.CultureInfo.InvariantCulture, out float sc)) specClampValue = sc;
        float specAaValue = 2f;
        if (float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_SPEC_AA"),
            System.Globalization.CultureInfo.InvariantCulture, out float sa)) specAaValue = sa;
        bool msBrdf = Environment.GetEnvironmentVariable("BALLISTIC_DX12_MS_BRDF") != "0";
        deferredCb.Write(new LightConstants {
            InvViewProj = Matrix4x4.Transpose(invVP),
            View = Matrix4x4.Transpose(ctx.View),
            LightDir = ctx.LightDir, LightColor = ctx.LightColor, Ambient = ctx.Ambient, CameraPos = ctx.CamPos,
            UseIBL = ctx.IblActiveThisFrame ? 1f : 0f,
            PrefilterMaxMip = ibl != null ? ibl.PrefilterMipCount - 1 : 0f,
            PunctualCount = clusteredLights.LightCount,
            ScreenSize = new Vector2(targetW, targetH),
            ClusterNearFar = new Vector2(CameraNear, CameraFar),
            UseRtShadows = ctx.RtShadowsThisFrame ? 1f : 0f,
            SpecClamp = specClampValue,
            SpecAaStrength = specAaValue,
            UseSsao = ctx.Doors.Ssao && ctx.PostFX.SSAOEnabled ? 1f : 0f,
            UseIBLDiffuse = (Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_FIX") != "0" && !ctx.GiActiveThisFrame) ? 1f : 0f,
            UseIBLSpecular = 0f,
            UseCapsuleShadows = ctx.CapsuleShadowsThisFrame ? 1f : 0f,
            ViewProjFwd = Matrix4x4.Transpose(ctx.ViewProj),
            UseVsm = (ctx.VsmActiveThisFrame && ctx.Vsm != null) ? 1f : 0f,
            VsmLevels = ctx.Vsm != null ? ctx.Vsm.Levels : 0f,
            VsmTexel = ctx.Vsm != null ? 1f / ctx.Vsm.Resolution : 0f,
            VsmLevel0Extent = ctx.Vsm != null ? ctx.Vsm.Level0Extent : 0f,
            VsmCamPos = ctx.CamPos,
            MsBrdfEnabled = msBrdf ? 1f : 0f,
        });

        VsmConstants vsmConsts = default;
        if (ctx.VsmActiveThisFrame && ctx.Vsm != null) {
            unsafe {
                for (int lvl = 0; lvl < ctx.Vsm.Levels && lvl < Dx12VirtualShadowMap.MaxLevels; lvl++) {
                    Matrix4x4 m = Matrix4x4.Transpose(ctx.Vsm.LightMatrices[lvl]);
                    float* dst = vsmConsts.Matrices + lvl * 16;
                    dst[0] = m.M11; dst[1] = m.M12; dst[2] = m.M13; dst[3] = m.M14;
                    dst[4] = m.M21; dst[5] = m.M22; dst[6] = m.M23; dst[7] = m.M24;
                    dst[8] = m.M31; dst[9] = m.M32; dst[10] = m.M33; dst[11] = m.M34;
                    dst[12] = m.M41; dst[13] = m.M42; dst[14] = m.M43; dst[15] = m.M44;
                }
            }
        }
        vsmCb.Write(vsmConsts);

        deferredSrvVisible.Reset();
        int b = deferredSrvVisible.AllocateRange(18);
        for (int i = 0; i < Dx12GBuffer.ShadedRtCount; i++)
            dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + i), gbuffer.ColorSrvCpu(i), heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 4), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 5), ibl.IrradianceSrv, heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 6), ibl.PrefilterSrv, heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 7), ibl.BrdfSrv, heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 8), shadowMap.SrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 9), clusteredLights.LightSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 10), clusteredLights.GridSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 11), clusteredLights.IndexSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 12),
            rtShadowMask != null ? rtShadowMask.ColorSrvCpu : gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 13),
            (ctx.Doors.Ssao && ctx.PostFX.SSAOEnabled) ? ctx.AoResult : gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 14), ltcTables.Ltc1SrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 15), ltcTables.Ltc2SrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 16),
            ctx.CapsuleShadowsThisFrame ? ctx.CapsuleShadowMask : gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 17),
            (ctx.VsmActiveThisFrame && ctx.Vsm != null) ? ctx.Vsm.SrvCpu : gbuffer.DepthSrvCpu, heapType);

        target.RenderColorOnlyCleared(cl => {
            cl.SetGraphicsRootSignature(deferredRootSig);
            cl.SetPipelineState(deferredPso);
            cl.SetDescriptorHeaps(deferredSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, deferredCb.Gpu);
            cl.SetGraphicsRootConstantBufferView(1, ctx.FrameCbAddress);
            cl.SetGraphicsRootDescriptorTable(2, deferredSrvVisible.Gpu(b));
            cl.SetGraphicsRootConstantBufferView(3, vsmCb.Gpu);
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
    }

    public void Dispose() {
        deferredPso?.Dispose();
        deferredRootSig?.Dispose();
        deferredCb?.Dispose();
        vsmCb?.Dispose();
        deferredSrvVisible?.Dispose();
        ltcTables?.Dispose();
    }
}
