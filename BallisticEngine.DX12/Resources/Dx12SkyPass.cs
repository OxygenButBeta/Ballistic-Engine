using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;        // PrimitiveTopology
using Vortice.Direct3D12;
using Vortice.Dxc;             // DxcShaderStage
using Vortice.DXGI;            // Format, SampleDescription

namespace BallisticEngine.DX12;

// Sky background: draw into the HDR color at the far plane, depth-testing the G-buffer depth (LEqual, no
// write). A ProceduralSky (DrawProcSky — the atmosphere/cloud march, or its fast baked-env-cube sample)
// takes precedence over an asset cubemap Skybox (DrawSkybox); both paths kept (matches GL).
//
// VERBATIM MOVE (chunk 8 of the pass-graph migration): the bodies of BuildSkybox/BuildProcSky and
// DrawSkybox/DrawProcSky are copied unchanged, only re-rooted onto `ctx`/this pass's own fields. No logic
// change → eyeball-unchanged + SHA==golden (Sky is byte-VISIBLE to the deterministic golden gate, unlike
// TAA/FSR — so SHA==golden is the real move oracle here). Copies the Dx12FogPass/AP template (blends into
// `target`, owns no resolution targets — just rootsig/PSO/CB/heap).
//
// Decision 4 / R2: the head resource transition lives at the TOP of Record — `gbuffer.DepthToReadOnly()`
// (the inline sky block did this right before drawing). The pass emits its OWN idempotent head transition,
// never relying on an upstream pass; the downstream Transparents pass re-asserts it for the Sky-disabled case.
//
// Event = Sky (350). doors.Sky = off under BARE-MINIMUM → the whole pass is skipped and the background keeps
// the HDR clear color (lit geometry still composites correctly — this just removes the sky to isolate it).
public sealed class Dx12SkyPass : IRenderPass, IDisposable {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.Sky;
    public string Name => "Sky";

    // The VERBATIM outer-if predicate: `if (doors.Sky)`.
    public bool Enabled(Dx12FrameContext ctx) => ctx.Doors.Sky;

    // PHASE-2 V1: reads the G-buffer depth (DepthToReadOnly head) and WRITES the HDR scene color (draws sky into
    // the un-lit background of `target` via RenderColorWithExternalDepth) — a ReadWrite since it preserves the
    // already-lit pixels the Deferred pass wrote and only paints where depth is far.
    public void Declare(Dx12PassBuilder b) {
        b.Read(b.Resource("GBuffer"));
        b.ReadWrite(b.Resource("SceneColor"));
        // PHASE-2 V3 (chunk 15): Sky's ONE shared-resource head transition is `gbuffer.DepthToReadOnly()` — the
        // depth becomes a read-only DSV for the LEqual-no-write sky draw. Derive it; the manual head in Record is
        // gated off when the barriers door is on (so the graph emits the single transition, not manual+derived).
        b.DeriveBarriers();
        b.Use(Dx12ResourceUsage.GBufferDepthReadOnly);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SkyboxConstants {
        public Matrix4x4 ViewProjNoTranslate;
        public Matrix4x4 SkyRotation;
        public float Exposure; public Vector3 Pad;
    }

    // MUST match ProceduralSky.hlsl's cbuffer AND Dx12IblBaker.ProcSkyConstants byte-for-byte.
    [StructLayout(LayoutKind.Sequential)]
    struct ProcSkyConstants {
        public Matrix4x4 ViewProjNoTranslate;
        public Vector3 SunDirection; public float SunAngularRadius;
        public Vector3 SunRadiance; public float SunDiskIntensity;
        public Vector3 GroundAlbedo; public float AirDensity;
        public float Haze, HazeAnisotropy, OzoneDensity, MultiScatter;
        public float Exposure, BakeFace; public Vector2 Pad0;
        // Volumetric clouds + cirrus + stars (GL Sky_Procedural.glsl parity).
        public float CloudsEnabled, CloudCoverage, CloudDensity, CloudAltitude;
        public float CloudThickness, CloudScale, CloudDetail, CloudAmbient;
        public Vector3 CloudWindOffset; public float CloudWindAngle;
        public float CirrusCoverage, StarIntensity; public Vector2 Pad1;
    }

    readonly Dx12Device dev;

    // Asset cubemap skybox.
    ID3D12RootSignature skyRootSig;
    ID3D12PipelineState skyPso;
    Dx12FrameCb<SkyboxConstants> skyCb;   // N-buffered, rewritten per frame (P0b frame overlap)
    Dx12DescriptorHeap skySrvVisible;   // one cube SRV copied per frame

    // Procedural sky. The FAST background path samples the baked env cube (procSkyBgPso, one cube fetch per
    // pixel); procSkyPso is the per-pixel atmosphere/cloud march, kept only as the fallback for the frame
    // before any env cube has been baked. Both share procSkyRootSig (CBV b0 + cube SRV table t0 + sampler s0).
    ID3D12RootSignature procSkyRootSig;
    ID3D12PipelineState procSkyPso;      // fallback: full SkyRadiance() march
    ID3D12PipelineState procSkyBgPso;    // primary: sample the baked env cube
    Dx12FrameCb<ProcSkyConstants> procSkyCb;   // N-buffered, rewritten per frame (P0b frame overlap)
    Dx12DescriptorHeap procSkyEnvSrvVisible;   // env cube SRV copied per frame for the background sample

    // VERBATIM BuildSkybox + BuildProcSky. Owns both rootsigs/PSOs/CBs/heaps (resolution-independent — no
    // Resize body).
    public unsafe Dx12SkyPass(Dx12Device device) {
        dev = device;
        BuildSkybox();
        BuildProcSky();
    }

    unsafe void BuildSkybox() {
        // Root sig: CBV b0 + 1 cube SRV table (t0) + static clamp sampler.
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
        // Depth: test LEqual, NO write — fills only far-plane (uncovered) pixels behind opaque geometry.
        var ds = DepthStencilDescription.Default;
        ds.DepthWriteMask = DepthWriteMask.Zero;
        ds.DepthFunc = ComparisonFunction.LessEqual;
        var psoDesc = new GraphicsPipelineStateDescription {
            RootSignature = skyRootSig, VertexShader = vs, PixelShader = ps,
            InputLayout = null,   // SV_VertexID cube, no vertex buffer
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
        // Root sig: ProcSky CBV (b0) + env cube SRV table (t0) + a linear-clamp sampler (s0). The CBV still
        // drives the march fallback; the SRV/sampler feed the fast env-cube background sample.
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

    // VERBATIM the inline sky block: head DepthToReadOnly, then draw the sky into the HDR color at the far
    // plane testing the G-buffer depth (LEqual, no write). ProceduralSky takes precedence over an asset cubemap.
    public unsafe void Record(Dx12FrameContext ctx) {
        Dx12GBuffer gbuffer = ctx.GBuffer;
        Dx12OffscreenTarget target = ctx.Target;

        // head transition (R2): emit our own (the inline block did this here). PHASE-2 V3: skip the manual head
        // when derived barriers are active (the graph emitted it before Record).
        if (!ctx.BarriersDerived) gbuffer.DepthToReadOnly();
        target.RenderColorWithExternalDepth(gbuffer.DsvHandle, cl => {
            if (ProceduralSky.Active is not null)
                DrawProcSky(cl, ctx);
            else
                DrawSkybox(cl, ctx);
        });
    }

    // Draw the procedural atmosphere as the far-plane background (pure-ALU march by view direction). The
    // sun dir/radiance come from ctx.LightDir/ctx.LightColor (== ToNumerics(light.Direction/Color) — the
    // orchestrator builds them that way, bit-identical to the old `light` param).
    unsafe void DrawProcSky(ID3D12GraphicsCommandList4 cl, Dx12FrameContext ctx) {
        ProceduralSky sky = ProceduralSky.Active;
        if (sky is null) return;

        Matrix4x4 view = ctx.View, proj = ctx.Proj;
        Matrix4x4 viewNoT = view; viewNoT.M41 = 0; viewNoT.M42 = 0; viewNoT.M43 = 0;
        // Sun: DirectionalLight drives it (ctx.LightDir is TOWARD the light = toward the sun).
        Vector3 sunDir = ctx.LightDir;
        if (sunDir.LengthSquared() < 1e-8f) sunDir = Vector3.UnitY;
        sunDir = Vector3.Normalize(sunDir);
        float sunAngularRadius = (DirectionalLight.Instance?.AngularDiameter ?? 0.53f) * 0.5f * (MathF.PI / 180f);

        float cloudTime = Dx12SkyCloudParams.CloudTime(sky);
        var sc = new ProcSkyConstants {
            ViewProjNoTranslate = Matrix4x4.Transpose(viewNoT * proj),
            SunDirection = sunDir, SunAngularRadius = MathF.Max(sunAngularRadius, 1e-4f),
            SunRadiance = ctx.LightColor, SunDiskIntensity = MathF.Max(sky.SunDiskIntensity, 0f),
            GroundAlbedo = sky.GroundColor, AirDensity = MathF.Max(sky.AirDensity, 0f),   // GroundColor is System.Numerics.Vector3 (ToNumerics was identity)
            Haze = MathF.Max(sky.Haze, 0f), HazeAnisotropy = Math.Clamp(sky.HazeAnisotropy, 0f, 0.99f),
            OzoneDensity = MathF.Max(sky.OzoneDensity, 0f), MultiScatter = MathF.Max(sky.MultipleScattering, 1f),
            Exposure = MathF.Max(sky.Exposure, 0f),
            // Volumetric clouds + cirrus + stars (clamps mirror GLProceduralSkyPass).
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

        // FAST PATH: the IBL baker already rendered the full SkyRadiance() kernel (atmosphere + clouds +
        // cirrus + stars + sun disk) into the env cube this frame — sample it instead of marching the whole
        // atmosphere again for every screen pixel. One cube fetch/pixel vs thousands of ALU/pixel; this was
        // the FPS sink. Only the first frame (before any bake) falls back to the per-pixel PSMain march.
        // BALLISTIC_DX12_SKY_MARCH=1 forces the old per-pixel march (A/B + escape hatch).
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
        // View with translation stripped (the sky cube is centred on the camera).
        Matrix4x4 viewNoT = view; viewNoT.M41 = 0; viewNoT.M42 = 0; viewNoT.M43 = 0;
        Vector3 euler = Skybox.Active.RotationEuler;
        Matrix4x4 rot = Matrix4x4.CreateRotationX(euler.X * (MathF.PI / 180f))
                      * Matrix4x4.CreateRotationY(euler.Y * (MathF.PI / 180f))
                      * Matrix4x4.CreateRotationZ(euler.Z * (MathF.PI / 180f));
        // The skybox texels are HDR scaled by sky.Exposure into RAW radiance, exactly like ProceduralSky
        // (DrawProcSky writes raw SunRadiance ~80000 and the composite auto-meters it). The old `* 1.0e-5f`
        // pre-divided the cube sky 100000x BELOW the composite's lux-scaled metering range → the sky crushed
        // to black (the exterior's "black sky"). Skybox.Exposure (~5000) alone lands an HDRI peak (~1) in the
        // same raw-radiance band as the procedural sky, so the metered exposure resolves it correctly.
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
