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
// REWRITE (dx12-aerial-perspective-rework): the old pass marched a fake analytic haze with a hardcoded
// lux-scaled blue tint (the flat blue-white veil). It now bakes a Hillaire FROXEL VOLUME each frame (the
// SAME atmosphere the sky shows) and just SAMPLES it by (screenUV, viewDistance) — physically correct,
// sky-matched colour, real exp(-beta*d) optical depth. All look tuning lives in the AerialPerspective
// Volume component (PostFX bridge); see Docs/Plans/dx12-aerial-perspective-rework.md.
//
// Event = AerialPerspective (400). Runs after Sky, before Transparents. Writes IN PLACE to the HDR scene
// color via RenderColorOnly (fixed-function blend: dest = dest*srcAlpha(T) + src(inscatter)).
public sealed class Dx12AerialPerspectivePass : IRenderPass, IDisposable {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.AerialPerspective;
    public string Name => "AerialPerspective";

    // The pass runs whenever the AP door is on AND a ProceduralSky drives the atmosphere. The per-frame
    // AerialPerspective volume toggle is honoured via the shader's `Enabled` constant (a clean no-op discard)
    // so the pass overhead is a single cheap bake + a discarded fullscreen draw when the volume turns it off.
    public bool Enabled(Dx12FrameContext ctx) => ctx.Doors.AerialPersp && ProceduralSky.Active is not null;

    // PHASE-2 V1: reads the G-buffer depth + the baked froxel volume and blends haze IN PLACE into the HDR
    // scene color (ReadWrite — it reads `target` via the blend and writes it back).
    public void Declare(Dx12PassBuilder b) {
        b.Read(b.Resource("GBuffer"));
        b.ReadWrite(b.Resource("SceneColor"));
        // AP's ONE shared-resource head transition is `gbuffer.DepthToShaderResource()` — same usage class as
        // SSAO/Fog. The froxel volume is pass-owned (the bake transitions it to PixelShaderResource itself).
        b.DeriveBarriers();
        b.Use(Dx12ResourceUsage.GBufferDepthShaderRead);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ApConstants {
        public Matrix4x4 InvViewProj;
        public Vector3 CameraPos; public float MaxDistance;   // froxel-volume far depth (m) — MUST match the bake
        public float Enabled; public Vector3 PadAp;           // 0 = pass is a clean no-op (shader discards)
    }

    readonly Dx12Device dev;
    readonly Dx12AerialPerspectiveLut lut;   // the froxel volume + its bake, baked at the head of Record
    ID3D12RootSignature apRootSig;           // ApConstants CBV (b0) + depth+volume SRV table (t0,t1) + 2 samplers
    ID3D12PipelineState apPso;
    ID3D12Resource apCb;
    unsafe byte* apCbMapped;
    Dx12DescriptorHeap apSrvVisible;         // depth + froxel volume, copied per frame (2 descriptors)

    public unsafe Dx12AerialPerspectivePass(Dx12Device device) {
        dev = device;
        lut = new Dx12AerialPerspectiveLut(device);

        // ApConstants CBV (b0) + a 2-SRV table (depth t0, froxel volume t1) + point sampler s0 + linear s1.
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
        // Sky-colour ambient tint for haze in shadow (Rayleigh-blue, engine-radiance scale) — the same anchor
        // the old pass used for SkyTint, now feeding the froxel volume's ambient in-scatter term.
        Vector3 skyTint = sunRadiance * new Vector3(0.10f, 0.16f, 0.32f);

        // BALLISTIC_DX12_AP_* env overrides (dev knobs) sit ON TOP of the PostFX/volume values so a paused A/B
        // capture can sweep without editing a scene. Default = the volume-driven PostFX value.
        float intensity = pf.AerialPerspectiveIntensity;
        if (float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_AP_STRENGTH"),
            System.Globalization.CultureInfo.InvariantCulture, out float s)) intensity = s;
        bool apEnabled = pf.AerialPerspectiveEnabled && intensity > 0f;

        // 1) Bake the froxel volume for this frame (compute dispatch on the frame list; leaves it as SRV). Bake
        // UNCONDITIONALLY so the volume is always left in PixelShaderResource — binding its SRV (t1, below)
        // while it sat in its initial UnorderedAccess state would be a resource-state / GBV error. When the
        // volume is disabled we bake at intensity 0 (≈ a clean, cheap empty volume) and the shader discards via
        // the Enabled constant anyway. 32^3 is cheap, so the always-on bake is the safe + simple choice.
        lut.Bake(invVP, camPos, sunDir, sunRadiance, skyTint, pSky, pf, apEnabled ? intensity : 0f);

        // 2) Fill the AP pass constants. MaxDistance MUST equal the bake's so the depth->slice map inverts it.
        *(ApConstants*)apCbMapped = new ApConstants {
            InvViewProj = Matrix4x4.Transpose(invVP),
            CameraPos = camPos, MaxDistance = MathF.Max(pf.AerialPerspectiveMaxDistance, 1f),
            Enabled = apEnabled ? 1f : 0f,
        };

        // Head transition (R2): emit our own unless the graph already derived barriers.
        if (!ctx.BarriersDerived) gbuffer.DepthToShaderResource();
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        dev.Device.CopyDescriptorsSimple(1, apSrvVisible.Cpu(0), gbuffer.DepthSrvCpu, heapType);   // t0 depth
        dev.Device.CopyDescriptorsSimple(1, apSrvVisible.Cpu(1), lut.SrvCpu, heapType);            // t1 froxel volume

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
        lut?.Dispose();
        apSrvVisible?.Dispose();
        apCb?.Dispose();
        apPso?.Dispose();
        apRootSig?.Dispose();
    }
}
