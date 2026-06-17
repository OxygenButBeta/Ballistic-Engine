using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;        // PrimitiveTopology
using Vortice.Direct3D12;
using Vortice.Dxc;             // DxcShaderStage
using Vortice.DXGI;            // Format, SampleDescription

namespace BallisticEngine.DX12;

// Aerial perspective: atmospheric haze on distant opaque geometry (#1 scale cue), blended over the
// sky+opaque HDR before transparents/fog. SEPARATE pass — never touches deferred lighting. Only when a
// ProceduralSky drives the atmosphere; BALLISTIC_DX12_AP=0 disables it.
//
// VERBATIM MOVE (chunk 5 of the pass-graph migration): the body of BuildAerialPerspective/DrawAerialPerspective
// is copied unchanged, only re-rooted onto `ctx`/this pass's own fields. No logic change → eyeball-unchanged +
// zero NEW GBV (a MOVE-only commit). Copies the Dx12SsaoPass template (the canonical leaf-post pass).
//
// Decision 4 / R2: the head resource transition (gbuffer.DepthToShaderResource) lives at the TOP of Record —
// the pass emits its OWN idempotent head transition, never relying on an upstream pass.
//
// Event = AerialPerspective (400). Today inline AP runs after Sky and before Transparents; under the graph it
// runs at its event slot (AerialPerspective=400 < Fog=550 < SSR/Reflections=600 < PostProcess/SSAO=650), which
// the graph.Execute() call (placed before composite) reproduces in the same relative order. It writes IN PLACE
// to `target` (the HDR scene color) via RenderColorOnly — no cross-pass output getter needed.
public sealed class Dx12AerialPerspectivePass : IRenderPass, IDisposable {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.AerialPerspective;
    public string Name => "AerialPerspective";

    // The VERBATIM outer-if predicate: `if (doors.AerialPersp && ProceduralSky.Active is not null)`.
    public bool Enabled(Dx12FrameContext ctx) => ctx.Doors.AerialPersp && ProceduralSky.Active is not null;

    [StructLayout(LayoutKind.Sequential)]
    struct ApConstants {
        public Matrix4x4 InvViewProj;
        public Vector3 CameraPos; public float Strength;
        public Vector3 SunDirection; public float Distance;
        public Vector3 SunRadiance; public float HazeAniso;
        public Vector3 SkyTint; public float AirDensity;
        public float Haze, MaxDistance, NearFade, Pad;   // NearFade: haze fades in over [NearFade, 2*NearFade] m (V3)
    }

    readonly Dx12Device dev;
    ID3D12RootSignature apRootSig;      // ApConstants CBV (b0) + depth SRV (t0) + sampler
    ID3D12PipelineState apPso;
    ID3D12Resource apCb;
    unsafe byte* apCbMapped;
    Dx12DescriptorHeap apSrvVisible;    // scene depth, copied per frame

    // VERBATIM BuildAerialPerspective. Owns rootsig/PSO/CB/heap (resolution-independent — no Resize body).
    public unsafe Dx12AerialPerspectivePass(Dx12Device device) {
        dev = device;
        // ApConstants CBV (b0) + a 1-SRV table (depth t0) + clamp sampler s0.
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var samp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        apRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { samp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("AerialPerspective.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "AerialPerspective.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "AerialPerspective.hlsl");

        // Same composite as fog: dest = dest*srcAlpha(transmittance) + src(inscatter).
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

        int apCbSize = (Marshal.SizeOf<ApConstants>() + 255) & ~255;
        apCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)apCbSize), ResourceStates.GenericRead);
        apCbMapped = apCb.Map<byte>(0);
        apSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, shaderVisible: true, framesInFlight: dev.FramesInFlight);
    }

    // VERBATIM DrawAerialPerspective. The call-site args (viewProj, camPos, apSunDir, lightColor=sunRadiance)
    // are re-derived from ctx: apSunDir = the normalized sun dir (UnitY fallback for a zero dir), exactly as the
    // inline call site computed it.
    public unsafe void Record(Dx12FrameContext ctx) {
        Matrix4x4 viewProj = ctx.ViewProj;
        Vector3 camPos = ctx.CamPos;
        Vector3 lightDir = ctx.LightDir;
        Vector3 sunDir = lightDir.LengthSquared() < 1e-8f ? Vector3.UnitY : Vector3.Normalize(lightDir);
        Vector3 sunRadiance = ctx.LightColor;
        Dx12GBuffer gbuffer = ctx.GBuffer;
        Dx12OffscreenTarget target = ctx.Target;

        Matrix4x4.Invert(viewProj, out Matrix4x4 invVP);
        var pSky = ProceduralSky.Active;
        // Sky-colour ambient tint for haze in shadow (Rayleigh-blue, engine-radiance scale).
        Vector3 skyTint = sunRadiance * new Vector3(0.10f, 0.16f, 0.32f);

        float strength = 1f;
        if (float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_AP_STRENGTH"),
            System.Globalization.CultureInfo.InvariantCulture, out float s)) strength = s;

        float distance = 1200f;  // haze half-distance in metres (scene-scale; env-tunable below)
        if (float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_AP_DISTANCE"),
            System.Globalization.CultureInfo.InvariantCulture, out float dd)) distance = dd;
        // V3 (fixes D2): fade the haze in over [NearFade, 2*NearFade] m so interiors / short views get ~no aerial
        // perspective (the lux-scaled SkyTint painted a blue veil on every opaque pixel even at ~10 m). 25 m fades
        // it in across 25–50 m: enclosed rooms stay clean, distant vistas keep the cue. =0 restores pre-V3 (door).
        float nearFade = 25f;
        if (float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_AP_NEARFADE"),
            System.Globalization.CultureInfo.InvariantCulture, out float nf)) nearFade = nf;
        *(ApConstants*)apCbMapped = new ApConstants {
            InvViewProj = Matrix4x4.Transpose(invVP),
            CameraPos = camPos, Strength = strength,
            SunDirection = sunDir, Distance = distance,
            SunRadiance = sunRadiance, HazeAniso = pSky is not null ? Math.Clamp(pSky.HazeAnisotropy, 0f, 0.95f) : 0.8f,
            SkyTint = skyTint, AirDensity = pSky is not null ? MathF.Max(pSky.AirDensity, 0f) : 1f,
            Haze = pSky is not null ? MathF.Max(pSky.Haze, 0f) : 1f,
            MaxDistance = 60000f, NearFade = nearFade,
        };

        gbuffer.DepthToShaderResource();   // head transition (R2): emit our own
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        dev.Device.CopyDescriptorsSimple(1, apSrvVisible.Cpu(0), gbuffer.DepthSrvCpu, heapType);

        target.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(apRootSig);
            cl.SetPipelineState(apPso);
            cl.SetDescriptorHeaps(apSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, apCb.GPUVirtualAddress);
            cl.SetGraphicsRootDescriptorTable(1, apSrvVisible.Gpu(0));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
    }

    public void Dispose() {
        apSrvVisible?.Dispose();
        apCb?.Dispose();
        apPso?.Dispose();
        apRootSig?.Dispose();
    }
}
