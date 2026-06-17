using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;        // PrimitiveTopology
using Vortice.Direct3D12;
using Vortice.Dxc;             // DxcShaderStage
using Vortice.DXGI;            // Format, SampleDescription

namespace BallisticEngine.DX12;

// Full-screen volumetric fog: march the air toward the camera (shadowed sun + sky in-scatter), blend
// (scatter, transmittance) over the scene color. Reads scene depth + the shadow cascades as SRVs.
//
// VERBATIM MOVE (chunk 5 of the pass-graph migration): the body of BuildFog/DrawFog is copied unchanged,
// only re-rooted onto `ctx`/this pass's own fields. No logic change → eyeball-unchanged + zero NEW GBV
// (a MOVE-only commit). Copies the Dx12SsaoPass template (the canonical leaf-post pass).
//
// Decision 4 / R2: the head resource transition (gbuffer.DepthToShaderResource) lives at the TOP of Record —
// the pass emits its OWN idempotent head transition, never relying on an upstream pass.
//
// Event = Fog (550). Today inline fog runs after SSGI/GI and before the reflections block (SSR); under the
// graph it runs at its event slot (AerialPerspective=400 < Fog=550 < SSR/Reflections=600 < PostProcess/SSAO=
// 650), which the graph.Execute() call (placed before composite) reproduces in the same relative order. It
// blends IN PLACE into `target` (the HDR scene color) via RenderColorOnly — no cross-pass output getter needed.
public sealed class Dx12FogPass : IRenderPass, IDisposable {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.Fog;
    public string Name => "Fog";

    // The VERBATIM outer-if predicate: `bool fogOn = (!doors.Minimal && PostFX.VolumetricEnabled) || doors.Fog;`.
    public bool Enabled(Dx12FrameContext ctx) =>
        (!ctx.Doors.Minimal && ctx.PostFX.VolumetricEnabled) || ctx.Doors.Fog;

    // PHASE-2 V1: reads the G-buffer depth (and samples the sun cascades, which live in the imported ShadowMap)
    // and blends fog IN PLACE into the HDR scene color (ReadWrite).
    public void Declare(Dx12PassBuilder b) {
        b.Read(b.Resource("GBuffer"));
        b.Read(b.Resource("ShadowMap"));
        b.ReadWrite(b.Resource("SceneColor"));
    }

    // The sun shadow map is built at this fixed size in DX12HDRenderer (const ShadowMapSize). The fog samples
    // the cascade array — the texel size must match the map. Mirror the orchestrator's const verbatim.
    const int ShadowMapSize = 2048;
    const int CascadeCount = 4;

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
    }

    readonly Dx12Device dev;
    ID3D12RootSignature fogRootSig;     // FogConstants CBV (b0) + depth+shadow SRV table (t0,t1) + sampler
    ID3D12PipelineState fogPso;
    ID3D12Resource fogCb;
    unsafe byte* fogCbMapped;
    Dx12DescriptorHeap fogSrvVisible;   // depth + shadow array, copied per frame

    // VERBATIM BuildFog. Owns rootsig/PSO/CB/heap (resolution-independent — no Resize body).
    public unsafe Dx12FogPass(Dx12Device device) {
        dev = device;
        // FogConstants CBV (b0) + a 2-SRV table (depth t0, shadow array t1) + clamp sampler s0.
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 2, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var samp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        fogRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { samp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("VolumetricFog.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "VolumetricFog.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "VolumetricFog.hlsl");

        // Blend: dest = dest * srcAlpha(transmittance) + src(scatter). Classic over-fog composite.
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

        fogPso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = fogRootSig, VertexShader = vs, PixelShader = ps, InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = blend,
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat },
            DepthStencilFormat = Format.Unknown, SampleDescription = new SampleDescription(1, 0),
        });

        int cbSize = (Marshal.SizeOf<FogConstants>() + 255) & ~255;
        fogCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        fogCbMapped = fogCb.Map<byte>(0);
        fogSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 2, shaderVisible: true, framesInFlight: dev.FramesInFlight);
    }

    // VERBATIM DrawFog. The call-site args (view, viewProj, camPos, light) are re-derived from ctx:
    // viewProj=ctx.ViewProj, camPos=ctx.CamPos, the sun dir/color = ctx.LightDir/ctx.LightColor (which the
    // orchestrator builds as ToNumerics(light.Direction/Color) — bit-identical). The cascade matrices come
    // from ctx (orchestrator-owned shared per-frame state, filled by RenderShadows before the graph runs).
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
        // Crude sky-ambient for fog in-scatter (engine-radiance scale; the fog Exposure constant matches
        // the opaque pre-exposure). A proper average-irradiance readback is a follow-up.
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
            ShadowMapTexel = 1f / ShadowMapSize, Exposure = 1.0e-5f,   // match the opaque pre-exposure
        };
        *(FogConstants*)fogCbMapped = fc;

        // depth → SRV (G-buffer owns it), shadow array already SRV from RenderShadows. Copy both into the
        // fog heap. After the sky pass the G-buffer depth is in DepthRead; bring it to PixelShaderResource.
        gbuffer.DepthToShaderResource();   // head transition (R2): emit our own
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        dev.Device.CopyDescriptorsSimple(1, fogSrvVisible.Cpu(0), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, fogSrvVisible.Cpu(1), shadowMap.SrvCpu, heapType);

        target.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(fogRootSig);
            cl.SetPipelineState(fogPso);
            cl.SetDescriptorHeaps(fogSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, fogCb.GPUVirtualAddress);
            cl.SetGraphicsRootDescriptorTable(1, fogSrvVisible.Gpu(0));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
    }

    public void Dispose() {
        fogSrvVisible?.Dispose();
        fogCb?.Dispose();
        fogPso?.Dispose();
        fogRootSig?.Dispose();
    }
}
