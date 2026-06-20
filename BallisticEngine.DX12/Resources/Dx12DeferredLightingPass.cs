using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;        // PrimitiveTopology
using Vortice.Direct3D12;
using Vortice.Dxc;             // DxcShaderStage
using Vortice.DXGI;            // Format, SampleDescription

namespace BallisticEngine.DX12;

// Deferred lighting: read the fat G-buffer + depth → PBR sun + IBL + shadows (cascade or RT mask) +
// clustered punctual → the HDR scene color (`target`). One full-screen draw; the pass is the CONSUMER of
// the G-buffer-as-SRV, so its head transition is `gbuffer.ToShaderResource()` (Decision 4 / R2 — every
// consumer emits its own head transition; idempotent no-op if an upstream already did it).
//
// VERBATIM MOVE (chunk 9 of the pass-graph migration): the bodies of BuildDeferredLighting/
// DrawDeferredLighting are copied unchanged, only re-rooted onto `ctx`/this pass's own fields. No logic
// change → SHA==golden (deferred shades every lit pixel, so the deterministic gate is the real move oracle
// here — a wrong move fails immediately, unlike a temporal pass). Copies the Dx12TransparentsPass template
// (draws into `target`, owns NO resolution targets → Resize is a no-op, R5-neutral).
//
// Event = OpaqueLighting (300) — the deferred-shading slot, after the G-buffer is filled (geometry + Hi-Z +
// punctual gather stay inline core) and after RT sun shadows ran (the orchestrator sets ctx.RtShadowsThisFrame
// before building ctx), before Sky (350). CORE-adjacent but the plan lists it as a pass (step E); it reads
// ctx light/IBL/cluster/rtShadow state and is always invoked (no outer-if → always enabled).
public sealed class Dx12DeferredLightingPass : IRenderPass, IDisposable {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.OpaqueLighting;
    public string Name => "Deferred";

    // The inline call had NO outer-if (DrawDeferredLighting was always invoked). So the pass is always enabled.
    public bool Enabled(Dx12FrameContext ctx) => true;

    // PHASE-2 V1: reads the G-buffer (as SRV), the sun shadow map, and the RT shadow mask; WRITES the HDR scene
    // color (it shades every lit pixel into `target` via RenderColorOnlyCleared — a full overwrite, the first
    // writer of SceneColor in the frame, so it's a pure Write not a ReadWrite).
    public void Declare(Dx12PassBuilder b) {
        b.Read(b.Resource("GBuffer"));
        b.Read(b.Resource("ShadowMap"));
        b.Read(b.Resource("RtShadowMask"));
        b.Write(b.Resource("SceneColor"));
        // PHASE-2 V3 (chunk 15): Deferred is the G-buffer-as-SRV CONSUMER — its ONE shared-resource head
        // transition is `gbuffer.ToShaderResource()` (the combined PIXEL|NON_PIXEL SRV state on ALL colors AND
        // depth). Derive it; the manual head in Record is gated off when the barriers door is on. The inline-core
        // RT sun shadows (which also reads the G-buffer as SRV, chunk 9) keeps its OWN head transition — it runs
        // outside the graph, so this migration doesn't affect it.
        b.DeriveBarriers();
        b.Use(Dx12ResourceUsage.GBufferShaderRead);
    }

    // Render-wide camera constants (the renderer's CameraNear/CameraFar, inlined — they're frame-invariant
    // const float on the orchestrator; the deferred CB's ClusterNearFar uses them).
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
        public float UseRtShadows; public float SpecClamp;   // SpecClamp: max per-light specular luma (V2 firefly cap; 0 = off)
        public float SpecAaStrength; public float UseSsao;   // V2: geometric specular AA strength (0 = off); UseSsao: GTAO into ambient
        public float UseIBLDiffuse; public float UseIBLSpecular; // 0 when Lumen V2 owns diffuse GI / RT+SSR own reflections
        public float UseCapsuleShadows; public float CapPad0, CapPad1, CapPad2; // >0.5 = multiply the capsule-shadow mask (t16) into the sun term
        public Matrix4x4 ViewProjFwd;                        // world → clip (transposed); contact-shadow march reprojection
    }

    readonly Dx12Device dev;
    ID3D12RootSignature deferredRootSig;   // LightConstants CBV(b0) + FrameConstants CBV(b1) + 16-SRV table + sampler
    ID3D12PipelineState deferredPso;
    Dx12FrameCb<LightConstants> deferredCb;   // P0b: N-buffered (FrameSlot-offset)
    Dx12DescriptorHeap deferredSrvVisible;  // 17 SRVs copied per frame: G0..G3, depth, irradiance, prefilter, BRDF, shadow, cluster lights/grid/index, RT shadow mask, GTAO, LTC mat (t14) + LTC amp (t15), capsule shadow mask (t16)
    Dx12LtcTables ltcTables;                // area/rect-light LTC lookup tables (t14/t15) — static, built once at init

    // BuildDeferredLighting moved VERBATIM into the ctor (re-rooted onto `dev`). clusteredLights stays
    // orchestrator-owned (the CPU froxel gather runs inline before deferred); the pass reads it via ctx.
    public unsafe Dx12DeferredLightingPass(Dx12Device device) {
        dev = device;
        var lightCbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.Pixel);
        var frameCbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.Pixel);
        // 17 SRVs: G0..G3, depth, irradiance, prefilter, BRDF, shadow (t0..t8), cluster lights/grid/index
        // (t9..t11), RT shadow mask (t12), GTAO (t13), LTC matrix-inverse table (t14), LTC amplitude table (t15),
        // capsule shadow mask (t16). The LTC tables (t14/t15) and the capsule mask (t16) are unread by the default
        // path (no area lights / no capsule casters → UseCapsuleShadows=0), so the table growth 14→17 is
        // byte-identical for the default path (the extra descriptors are bound but never sampled).
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 17, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var samp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        deferredRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None,
                new[] { lightCbv, frameCbv, srvTable }, new[] { samp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("DeferredLighting.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "DeferredLighting.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "DeferredLighting.hlsl");
        deferredPso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = deferredRootSig, VertexShader = vs, PixelShader = ps, InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat },
            DepthStencilFormat = Format.Unknown, SampleDescription = new SampleDescription(1, 0),
        });

        deferredCb = new Dx12FrameCb<LightConstants>(dev);
        deferredSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 17, shaderVisible: true, framesInFlight: dev.FramesInFlight);
        // Build the LTC tables once (CPU fit + GPU upload). Self-contained; no scene/asset dependency.
        ltcTables = new Dx12LtcTables(dev);
    }

    // DrawDeferredLighting moved VERBATIM. Re-rooted onto ctx: view/viewProj/camPos/light from ctx; IBL/
    // shadow/cluster resources from ctx; iblActiveThisFrame/rtShadowsThisFrame from ctx; the RT shadow mask
    // (orchestrator-owned, null when RT shadows never ran) and the FrameConstants CBV address from ctx.
    public unsafe void Record(Dx12FrameContext ctx) {
        var gbuffer = ctx.GBuffer;
        var ibl = ctx.Ibl;
        var shadowMap = ctx.ShadowMap;
        var clusteredLights = ctx.ClusteredLights;
        var rtShadowMask = ctx.RtShadowMask;
        var target = ctx.Target;
        int targetW = ctx.TargetW, targetH = ctx.TargetH;

        // R2 / Decision 4: deferred is the CONSUMER of the G-buffer-as-SRV — head transition lives here.
        // === DEFERRED LIGHTING: read the G-buffer + depth → PBR sun + IBL + shadows + punctual → HDR. ===
        // PHASE-2 V3: skip the manual head when derived barriers are active (the graph emitted it before Record).
        if (!ctx.BarriersDerived) gbuffer.ToShaderResource();

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Matrix4x4.Invert(ctx.ViewProj, out Matrix4x4 invVP);
        // V2 specular firefly cap (per-light specular luma clamp). Default on; BALLISTIC_DX12_SPEC_CLAMP tunes it
        // (0 = off, byte-identical to pre-V2). Lux-ish radiance scale → a high cap that only bites texel spikes.
        float specClampValue = 8000f;
        if (float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_SPEC_CLAMP"),
            System.Globalization.CultureInfo.InvariantCulture, out float sc)) specClampValue = sc;
        // V2 geometric specular AA: roughen noisy normals to kill normal-map sparkle. Default on; tune/disable
        // via BALLISTIC_DX12_SPEC_AA (0 = off, byte-identical). Strength scales the normal-derivative variance.
        float specAaValue = 2f;
        if (float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_SPEC_AA"),
            System.Globalization.CultureInfo.InvariantCulture, out float sa)) specAaValue = sa;
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
            // V2: per-light specular luma cap (firefly bound). The radiance scale is lux-ish (sun ~80000), so the
            // cap is high — it only bites single-texel NDF spikes, not broad highlights. BALLISTIC_DX12_SPEC_CLAMP
            // tunes it; =0 disables (byte-identical). Default 8000 (≈ a tenth of the sun radiance — outliers only).
            SpecClamp = specClampValue,
            SpecAaStrength = specAaValue,
            // GTAO into the ambient term — on only when AO is actually rendered this frame (door + volume enable).
            // Matches Dx12GtaoPass.Enabled so the t13 bind below holds the real AO target when this is 1.
            UseSsao = ctx.Doors.Ssao && ctx.PostFX.SSAOEnabled ? 1f : 0f,
            // Diffuse sky-IBL is DISABLED on the deferred path. It samples the env-irradiance cube by surface
            // normal with NO sky-visibility term (only short-range GTAO), so a CLOSED interior — whose walls never
            // see the sky — still ate the procedural sky's full (bright, sun-tinted) ambient and washed flat. The
            // Skybox path never baked IBL at all (UseIBL=0), so the two skies behaved completely differently
            // (user: "skybox ne yapıyorsa procedural de aynısını yapmalı"). Parity fix: NEITHER sky adds diffuse
            // sky-ambient here — diffuse indirect comes from Lumen GI alone (which DOES occlude via the TLAS).
            // Specular IBL stays (reflections; occluded separately). When Lumen is active it owned this anyway.
            UseIBLDiffuse = 0f,
            // Sky-IBL specular ALSO disabled on the deferred path (parity with the diffuse above). The prefiltered
            // cube has no sky-visibility, so a closed interior ate the procedural sky's bright sun-tinted average as
            // a broad untextured veil (the orange "tent" on a roofed Bistro hall). Reflections come from Lumen RT /
            // SSR (sky-visibility-aware); the Skybox path never baked IBL (UseIBL=0) so both skies now match.
            UseIBLSpecular = 0f,
            // Capsule shadows — multiply the analytic capsule-occlusion mask (t16) into the sun term when a
            // CapsuleShadowCaster ran this frame. 0 when no caster (mask unbound-effective) → byte-identical.
            UseCapsuleShadows = ctx.CapsuleShadowsThisFrame ? 1f : 0f,
            // Forward world→clip for the contact-shadow screen-space march (HLSL muls row-vector × matrix).
            ViewProjFwd = Matrix4x4.Transpose(ctx.ViewProj),
        });

        // Copy the 16 SRVs: G0..G3, depth, irradiance, prefilter, BRDF, shadow (t0..t8), cluster
        // lights/grid/index (t9..t11), RT shadow mask (t12), GTAO (t13), LTC mat (t14), LTC amp (t15).
        deferredSrvVisible.Reset();
        int b = deferredSrvVisible.AllocateRange(17);
        // Only the 4 SHADED G-buffer RTs feed lighting (G0..G3 → t0..t3); the motion RT (RT4) is for TAA/FSR.
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
        // RT shadow mask (t12) — the real mask when RT shadows ran this frame, else a valid unused fallback.
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 12),
            rtShadowMask != null ? rtShadowMask.ColorSrvCpu : gbuffer.DepthSrvCpu, heapType);
        // GTAO (t13) — the blurred AO from Dx12GtaoPass (event 200, runs before this) when AO is on, else a
        // valid unused fallback. UseSsao gates the sample, so the fallback's contents never affect the output.
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 13),
            (ctx.Doors.Ssao && ctx.PostFX.SSAOEnabled) ? ctx.AoResult : gbuffer.DepthSrvCpu, heapType);
        // LTC tables (t14 = matrix inverse, t15 = amplitude/Fresnel) — static area-light data, bound every
        // frame. Point/spot scenes never sample them (the rect type-branch in ShadePunctual is skipped), so
        // their presence is byte-identical for the default path.
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 14), ltcTables.Ltc1SrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 15), ltcTables.Ltc2SrvCpu, heapType);
        // Capsule shadow mask (t16) — the real mask when a CapsuleShadowCaster ran this frame, else a valid
        // unused fallback. UseCapsuleShadows gates the sample, so the fallback's contents never affect output.
        dev.Device.CopyDescriptorsSimple(1, deferredSrvVisible.Cpu(b + 16),
            ctx.CapsuleShadowsThisFrame ? ctx.CapsuleShadowMask : gbuffer.DepthSrvCpu, heapType);

        target.RenderColorOnlyCleared(cl => {
            cl.SetGraphicsRootSignature(deferredRootSig);
            cl.SetPipelineState(deferredPso);
            cl.SetDescriptorHeaps(deferredSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, deferredCb.Gpu);
            cl.SetGraphicsRootConstantBufferView(1, ctx.FrameCbAddress);
            cl.SetGraphicsRootDescriptorTable(2, deferredSrvVisible.Gpu(b));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
    }

    public void Dispose() {
        deferredPso?.Dispose();
        deferredRootSig?.Dispose();
        deferredCb?.Dispose();
        deferredSrvVisible?.Dispose();
        ltcTables?.Dispose();
    }
}
