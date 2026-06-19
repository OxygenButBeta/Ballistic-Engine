using System;
using System.Numerics;
using System.Runtime.InteropServices;
using BallisticEngine;         // RuntimeSet, IStaticMeshRenderer, ReflectionMode, PostProcessSettings
using Vortice.Direct3D;        // PrimitiveTopology
using Vortice.Direct3D12;
using Vortice.Dxc;             // DxcShaderStage
using Vortice.DXGI;            // Format, SampleDescription

namespace BallisticEngine.DX12;

// Reflections — the SINGLE mode-branching reflections pass (plan decision F / trap 4: per-mode passes would
// break the EnsureRtReflections-fallback, so reflections is ONE pass that branches internally). It folds the
// inline reflections block (DX12HDRenderer ~1649) + extracts the SSR resources chunk 5 DEFERRED to here:
//   - SSR (half-res view-space march → depth-aware Fresnel combine into the scene color)
//   - RT reflections (DxrReflections trace per pixel → REUSES the SSR combine) with the world-radiance hit
//     shading (sun + punctual + the DDGI world-cache field as ambient, read from ctx.Dxr.Ddgi)
//   - the rtReflWanted && EnsureRtReflections() ? DrawRtReflections : DrawSsr branch + the !sceneAS.Valid →
//     DrawSsr fallback (verbatim) — DrawRtReflections SHARES every SSR resource (ssrTarget/ssrScene/ssrCb/
//     ssrRootSig/ssrCombinePso/ssrSrvVisible) AND falls back to DrawSsr, which is exactly WHY chunk 5 deferred
//     SSR to this unified pass.
//
// Event = Reflections (600). Enabled = !Minimal && PostFX.SsrEnabled && SsrIntensity>0 (the verbatim outer-if;
// doors.Minimal forces SSR off, re-enabled at the SSR stage via the Ssr volume / a forced PostFX). The RT-vs-
// SSR branch (including the BALLISTIC_DX12_RT_REFLECTIONS env / PostFX.ReflectionMode read) lives in Record.
// The shared DXR substrate (sceneAS / device5 / dxr-availability / rtGeometry / ddgi) lives in ctx.Dxr,
// shared with RT shadows (inline) + the GI pass. Wrap ORCHESTRATION only — the DXR closest-hit is frozen.
public sealed class Dx12ReflectionsPass : IRenderPass, IDisposable {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.Reflections;
    public string Name => "Reflections";

    // The verbatim outer-if from the inline reflections block (DX12HDRenderer ~1649). When false the pass is
    // skipped entirely (no SSR, no RT reflections) — exactly as the inline `if(...)` did.
    // CHUNK4: BALLISTIC_DX12_REFLECTIONS=1 force-enables the pass headlessly (SsrEnabled is default-off in the
    // user's reflections-WIP, so the A/B harness has no other way to exercise the frozen-cascade-fed reflection).
    // =0 force-off. Default (unset) = the verbatim PostFX gate (byte-identical).
    public bool Enabled(Dx12FrameContext ctx) {
        if (reflForceEnvUnread) { reflForceEnv = Environment.GetEnvironmentVariable("BALLISTIC_DX12_REFLECTIONS"); reflForceEnvUnread = false; }
        if (ctx.Doors.Minimal) return false;
        // Force-enable door (A/B harness): run regardless of SsrIntensity — test scenes carry no Reflection volume
        // so SsrIntensity is 0; the old `SsrIntensity>0` gate made RT reflections un-testable headlessly. Record
        // substitutes a default intensity when the door forces it on with intensity 0 (ForcedIntensity below).
        if (reflForceEnv == "1") return true;
        if (reflForceEnv == "0") return false;
        if (ctx.PostFX.SsrIntensity <= 0f) return false;
        return ctx.PostFX.SsrEnabled;
    }
    // The intensity the A/B door uses when forcing the pass on against a scene with no Reflection volume (SsrIntensity 0).
    float ForcedIntensity(Dx12FrameContext ctx) =>
        reflForceEnv == "1" && ctx.PostFX.SsrIntensity <= 0f ? EnvF("BALLISTIC_DX12_REFLECTIONS_INTENSITY", 1f) : ctx.PostFX.SsrIntensity;
    static float EnvF(string n, float f) => float.TryParse(Environment.GetEnvironmentVariable(n),
        System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : f;
    string reflForceEnv; bool reflForceEnvUnread = true;

    // Temporal reflection denoise master switch. Default ON (kills static-view jitter); BALLISTIC_DX12_REFL_
    // NOTEMPORAL=1 turns it OFF — the surface-motion reprojection can ERASE reflections while the camera moves
    // (the "reflections vanished when I move" bug), so this is the escape hatch / A-B door.
    static bool? reflTemporalEnabled;
    static bool ReflTemporalEnabled =>
        reflTemporalEnabled ??= Environment.GetEnvironmentVariable("BALLISTIC_DX12_REFL_NOTEMPORAL") != "1";

    // P2 — RT-reflections + card env doors cached on first use (process-scoped; were re-read every record).
    static string? rtrEnvCached; static bool rtrEnvRead;
    static string RtrEnv() { if (!rtrEnvRead) { rtrEnvCached = Environment.GetEnvironmentVariable("BALLISTIC_DX12_RT_REFLECTIONS"); rtrEnvRead = true; } return rtrEnvCached!; }
    static bool? reflNoCards;
    static bool ReflCardsAllowed => !(reflNoCards ??= Environment.GetEnvironmentVariable("BALLISTIC_DX12_REFL_NOCARDS") == "1");

    // PHASE-2 V1: reads the G-buffer (depth + normal/roughness for the SSR march) and read-modify-writes the HDR
    // scene color (marches reflections from `target`, then CopyColorFrom(ssrScene) back into `target`). RT
    // reflections additionally use the DXR AS (inline-core in V1) — declaring G-buffer + SceneColor suffices for
    // the V1 order/cull (RT reflections excluded from the golden gate).
    public void Declare(Dx12PassBuilder b) {
        b.Read(b.Resource("GBuffer"));
        b.ReadWrite(b.Resource("SceneColor"));
        // PHASE-2 V3 (chunk 16): derive ONLY the SSR-path (DrawSsr) shared boundary head — the SSR march reads the
        // lit HDR scene color + G-buffer depth as SRVs (target.ColorToShaderResource() +
        // gbuffer.DepthToShaderResource(), DrawSsr head, where target == ctx.SceneColor). Derive both. LEFT INLINE:
        //   - the RT branch's gbuffer.DepthToNonPixelShaderResource() (DXR raygen depth read, OUT of V3 scope),
        //   - the RT branch's own target.ColorToShaderResource() + its combine-tail gbuffer.DepthToShaderResource(),
        //   - the pass-private scratch transitions (ssrTarget/ssrScene) — mid-pass, not pass-boundary heads.
        // DrawSsr is also the !sceneAS.Valid fallback from DrawRtReflections; the derived (or RT-branch) heads
        // already ran by then, so the gated heads are idempotent no-ops. RT reflections excluded from the golden
        // gate; the deterministic matrix exercises only the SSR (DrawSsr) path.
        b.DeriveBarriers();
        b.Use(Dx12ResourceUsage.SceneColorShaderRead);
        b.Use(Dx12ResourceUsage.GBufferDepthShaderRead);
    }

    readonly Dx12Device dev;

    // === SSR: half-res view-space reflection march → combine (depth-aware upsample, lerp into HDR color). ===
    ID3D12RootSignature ssrRootSig;     // SsrConstants CBV(b0) + 5-SRV table(color/depth/normal/material/ssr) + sampler
    ID3D12PipelineState ssrMarchPso, ssrCombinePso;
    ID3D12Resource ssrCb;
    unsafe byte* ssrCbMapped;
    Dx12OffscreenTarget ssrTarget;      // half-res RGBA16F reflection (rgb + strength); also RT reflections' UAV output
    Dx12OffscreenTarget ssrScene;       // full-res scratch: combine writes here, then copied back to `target`
    Dx12DescriptorHeap ssrSrvVisible;   // 5 SRVs per pass (10-slot ring)
    // RT-reflection TEMPORAL denoise: RT reflections are mirror rays (no jitter) but the HIT samples the DDGI
    // world cache, which changes frame-to-frame — and reflections have NO denoiser (unlike diffuse GI's OIDN +
    // temporal), so that churn shows as raw JITTER in the mirror (the user: "disabling reflections killed the
    // jitter"). A light motion-reprojected EMA over the half-res reflection target smooths it. SSR (screen march)
    // is stable and skips this. Ping-pong; history is cross-frame so NEVER pooled.
    Dx12OffscreenTarget ssrHistoryA, ssrHistoryB;
    ID3D12PipelineState ssrTemporalPso;
    bool ssrHistWriteB, ssrHistValid;
    [StructLayout(LayoutKind.Sequential)]
    struct SsrConstants {
        public Matrix4x4 Projection; public Matrix4x4 InvProjection; public Matrix4x4 ViewMatrix;
        public float Intensity; public Vector3 Pad;
        public Vector2 TexelSize; public Vector2 Pad2;
    }

    // === DXR ray-traced reflections (Reflection volume SSR-vs-RT dropdown: PostFX.ReflectionMode) ===
    ID3D12RootSignature rtReflRootSig;          // HeapDirectlyIndexed; CBV b0/b1/b2 + table{t0-t6,u0} + root SRV t7/t8/t9/t10 + s0/s1
    ID3D12StateObject rtReflPso;
    ID3D12Resource rtReflSbt;
    // P0b: the three single-value-per-frame RT-reflection CBs are N-buffered (FrameSlot-offset). ssrCb stays a
    // plain single-slot CB — it is WRITTEN+BOUND multiple times per frame with DIFFERENT values (march, temporal,
    // combine), an intra-frame multi-write that Dx12FrameCb's per-FRAME slotting does not model. Built lazily in
    // EnsureRtReflections (DXR may be unavailable / RT reflections never requested), so these allocate there.
    Dx12FrameCb<RtReflConstants> rtReflCb;
    Dx12FrameCb<RtGiSun> rtReflSunCb;
    Dx12FrameCb<RtReflGridConstants> rtReflGridCb;
    bool rtReflBuilt;
    const int RtSbtSlot = 64;                   // shader-table record alignment
    // Phase-8 reflection table reserves its OWN 8-slot tail of the bindless heap, BELOW the ScreenProbe tail so
    // the four reservations (RtRefl < ScreenProbe < DDGI < RtGi) never collide. Slots used (8): t0 TLAS, t1 depth,
    // t2 normal, t3 material, t4 irr cube, t5 prefilter cube, t6 DDGI irr atlas, u0 ssrTarget. R1.1: the base is
    // no longer a hand-written `16384 - 32` magic number — it comes from the single Dx12BindlessTail allocator
    // (compile-time-asserted, byte-identical to the old constant; see Dx12BindlessTail.cs).
    const int RtReflTableBase = Dx12BindlessTail.RtReflTableBase;
    [StructLayout(LayoutKind.Sequential)]
    struct RtReflConstants {
        public Matrix4x4 InvViewProj; public Vector3 CameraPos; public float Intensity;
        public float PrefilterMaxMip; public float NormalBias; public float UseCards; public float Unused1;  // UseCards: P5 — sample the Lumen card cache at hits
    }
    [StructLayout(LayoutKind.Sequential)]
    struct RtGiSun { public Vector3 SunDir; public float NormalBias; public Vector3 SunColor; public float LightCount; }
    // The DdgiGrid CBV (b2) was always written as a zeroed 256-byte block (the closest-hit reads it but the
    // current path supplies no DDGI grid → all-zero). A 256-byte blittable struct so it round-trips through
    // Dx12FrameCb; written as `default` (all-zero, byte-identical to the old Span<byte>.Clear()).
    [StructLayout(LayoutKind.Sequential, Size = 256)]
    struct RtReflGridConstants { }

    // BuildSsr moved VERBATIM into the ctor (re-rooted onto dev). The SSR PSOs/CB/heap are built here; the SSR
    // targets allocate in Resize, called at the end of the ctor (the inline BuildSsr called AllocSsrTarget(); the
    // SSAO/TAA/Composite passes follow the same ctor(dev,w,h) → Resize pattern) so the first frame already has
    // valid targets. The RT-reflection pipeline (rtRefl*) stays LAZY (EnsureRtReflections on first RT use)
    // exactly as inline — DXR may be unavailable / RT reflections may never be requested.
    public unsafe Dx12ReflectionsPass(Dx12Device device, int width, int height) {
        dev = device;
        BuildSsr();
        Resize(width, height);
    }

    // ===== ENABLED-PASS RECORD: the inline reflections block (DX12HDRenderer ~1649) moved VERBATIM. =====
    // rtReflWanted && EnsureRtReflections() ? DrawRtReflections : DrawSsr. The inline TimePass("Reflections:RT")
    // tag is dropped — the GRAPH already times the pass under Name ("Reflections").
    public unsafe void Record(Dx12FrameContext ctx) {
        Dx12RenderTargetPool.PoolBarrier(ctx.Dev, "ssrTarget", "ssrScene");   // V2: aliasing barrier + discard the produced placed targets (no-op when pool off)
        string rtrEnv = RtrEnv();
        bool rtReflWanted = rtrEnv == "1" || (rtrEnv != "0" && ctx.PostFX.ReflectionMode == ReflectionMode.RayTraced);
        if (rtReflWanted && EnsureRtReflections(ctx))
            DrawRtReflections(ctx);
        else
            DrawSsr(ctx);
    }

    // ============================== SSR ==============================

    // Screen-space reflections (volume-driven): half-res view-space march reads the lit HDR color + G-buffer →
    // ssrTarget; combine depth-aware-upsamples + lerps into the scene color (via ssrScene, copied back).
    unsafe void DrawSsr(Dx12FrameContext ctx) {
        var dev = ctx.Dev; var target = ctx.SceneColor; var gbuffer = ctx.GBuffer;
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Matrix4x4 view = ctx.View, proj = ctx.Proj;
        Matrix4x4.Invert(proj, out Matrix4x4 invProj);
        *(SsrConstants*)ssrCbMapped = new SsrConstants {
            Projection = Matrix4x4.Transpose(proj), InvProjection = Matrix4x4.Transpose(invProj),
            ViewMatrix = Matrix4x4.Transpose(view),
            Intensity = ForcedIntensity(ctx),
            TexelSize = new Vector2(1f / ssrTarget.Width, 1f / ssrTarget.Height),
        };

        // Both passes need the HDR color + G-buffer as SRVs. The G-buffer is already SRV; bring color to SRV.
        // R2 / Decision 4: reflections is a consumer of color + G-buffer-as-SRV — head transitions live here.
        // PHASE-2 V3: skip the manual SceneColor + depth heads when derived barriers are active (the graph emitted
        // ctx.SceneColor.ColorToShaderResource() + gbuffer.DepthToShaderResource() before Record). Idempotent.
        if (!ctx.BarriersDerived) {
            target.ColorToShaderResource();
            gbuffer.DepthToShaderResource();
        }

        // March (half-res) → ssrTarget. SRV slots: color t0, depth t1, normal t2, material t3, (ssr t4 unused).
        ssrSrvVisible.Reset();
        int mb = ssrSrvVisible.AllocateRange(5);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(mb + 0), target.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(mb + 1), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(mb + 2), gbuffer.ColorSrvCpu(1), heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(mb + 3), gbuffer.ColorSrvCpu(2), heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(mb + 4), ssrTarget.ColorSrvCpu, heapType);
        ssrTarget.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(ssrRootSig); cl.SetPipelineState(ssrMarchPso);
            cl.SetDescriptorHeaps(ssrSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, ssrCb.GPUVirtualAddress);
            cl.SetGraphicsRootDescriptorTable(1, ssrSrvVisible.Gpu(mb));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });

        // TEMPORAL DENOISE the half-res reflection (same pass RT uses) — kills the static-view march/Fresnel jitter
        // before the combine. The motion gate passes the live reflection through under any real camera motion, so
        // moving views stay sharp. The combine then reads the DENOISED target instead of the raw ssrTarget.
        ssrTarget.ColorToShaderResource();
        // BALLISTIC_DX12_REFL_NOTEMPORAL=1 bypasses the temporal denoise (the motion-reprojected EMA that can
        // ERASE reflections while the camera moves — surface-motion reprojection pulls the wrong reflected texel).
        Dx12OffscreenTarget reflForCombine = ReflTemporalEnabled
            ? DenoiseReflectionTemporal(ctx, gbuffer)
            : ssrTarget;

        // Combine (full-res) → ssrScene, reading scene color (t0), depth (t1), denoised reflection (t4). The
        // temporal pass overwrote ssrCb (Intensity = hasHistory) — restore the full combine SsrConstants.
        Matrix4x4.Invert(proj, out Matrix4x4 invProjC);
        *(SsrConstants*)ssrCbMapped = new SsrConstants {
            Projection = Matrix4x4.Transpose(proj), InvProjection = Matrix4x4.Transpose(invProjC),
            ViewMatrix = Matrix4x4.Transpose(view), Intensity = ForcedIntensity(ctx),
            TexelSize = new Vector2(1f / ssrTarget.Width, 1f / ssrTarget.Height),
        };
        ssrSrvVisible.Reset();
        int cb = ssrSrvVisible.AllocateRange(5);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 0), target.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 1), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 2), gbuffer.ColorSrvCpu(1), heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 3), gbuffer.ColorSrvCpu(2), heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 4), reflForCombine.ColorSrvCpu, heapType);
        ssrScene.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(ssrRootSig); cl.SetPipelineState(ssrCombinePso);
            cl.SetDescriptorHeaps(ssrSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, ssrCb.GPUVirtualAddress);
            cl.SetGraphicsRootDescriptorTable(1, ssrSrvVisible.Gpu(cb));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
        ssrScene.ColorToShaderResource();
        target.CopyColorFrom(ssrScene);   // the reflected scene becomes the new scene color
    }

    // TEMPORAL DENOISE (shared by SSR + RT): motion-reproject + EMA the half-res `ssrTarget` reflection into the
    // ping-pong history, returning the smoothed target the combine should read. PSTemporal hard-gates on screen
    // motion (~static → heavy EMA = kills the frame-to-frame churn that shows as jitter; any real motion → passes
    // the live reflection straight through, so a moving view/mirror never smears). SSR's half-res march + Fresnel
    // edges flicker on a static view exactly like RT's cache-fed mirror does, so both feed the SAME denoiser.
    // Pre: ssrTarget is in ColorToShaderResource. Post: the returned target is in ColorToShaderResource.
    unsafe Dx12OffscreenTarget DenoiseReflectionTemporal(Dx12FrameContext ctx, Dx12GBuffer gbuffer) {
        var dev = ctx.Dev;
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Dx12OffscreenTarget histRead = ssrHistWriteB ? ssrHistoryA : ssrHistoryB;
        Dx12OffscreenTarget histWrite = ssrHistWriteB ? ssrHistoryB : ssrHistoryA;
        histRead.ColorToShaderResource();
        *(SsrConstants*)ssrCbMapped = new SsrConstants {
            Intensity = ssrHistValid ? 1f : 0f,   // repurposed as the hasHistory flag for PSTemporal
            TexelSize = new Vector2(1f / ssrTarget.Width, 1f / ssrTarget.Height),
        };
        ssrSrvVisible.Reset();
        int tb = ssrSrvVisible.AllocateRange(5);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(tb + 0), ssrTarget.ColorSrvCpu, heapType);          // t0 current
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(tb + 1), histRead.ColorSrvCpu, heapType);           // t1 history
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(tb + 2), gbuffer.ColorSrvCpu(Dx12GBuffer.MotionRtIndex), heapType); // t2 motion
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(tb + 3), ssrTarget.ColorSrvCpu, heapType);          // t3 (unused, valid)
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(tb + 4), ssrTarget.ColorSrvCpu, heapType);          // t4 (unused, valid)
        histWrite.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(ssrRootSig); cl.SetPipelineState(ssrTemporalPso);
            cl.SetDescriptorHeaps(ssrSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, ssrCb.GPUVirtualAddress);
            cl.SetGraphicsRootDescriptorTable(1, ssrSrvVisible.Gpu(tb));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
        histWrite.ColorToShaderResource();
        ssrHistWriteB = !ssrHistWriteB; ssrHistValid = true;
        return histWrite;
    }

    // ============================== RT reflections ==============================

    // Lazily build the DXR reflection pipeline. Reuses the shared device5 + sceneAS + rtGeometry (ctx.Dxr).
    // Returns false (→ SSR fallback) when DXR is unavailable.
    unsafe bool EnsureRtReflections(Dx12FrameContext ctx) {
        if (!ctx.Dxr.CheckAvailable("RTReflections")) return false;
        if (rtReflBuilt) return true;
        rtReflBuilt = true;

        var dev = ctx.Dev;
        var device5 = ctx.Dxr.Device5;

        // PHASE 8 root sig (mirrors rtGiRootSig — the closest-hit decodes the hit bindlessly via
        // ResourceDescriptorHeap[], so HeapDirectlyIndexed + the table descriptors live in the bindless tail):
        //   CBV b0 ReflConstants | CBV b1 RtGiSun | CBV b2 DdgiGrid | table{SRV t0-t6, UAV u0} |
        //   root SRV t7 GpuMaterials | t8 RtInstance[] | t9 Lights | t10 ProbeState + static clamp s0 + wrap s1.
        var cbv0 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var cbv1 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.All);
        var cbv2 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(2, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 7, baseShaderRegister: 0);  // t0-t6
        var uavRange = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 0);
        var table = new RootParameter1(new RootDescriptorTable1(srvRange, uavRange), ShaderVisibility.All);
        var matSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(7, 0), ShaderVisibility.All);
        var instSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(8, 0), ShaderVisibility.All);
        var lightSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(9, 0), ShaderVisibility.All);
        var probeSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(10, 0), ShaderVisibility.All);
        var cardSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(11, 0), ShaderVisibility.All);  // P5 t11 CardRadiance
        var metaSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(12, 0), ShaderVisibility.All);  // P5 t12 LumenInstanceMeta
        var triClusterSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(13, 0), ShaderVisibility.All);  // #2A t13 TriToCluster
        var clampSamp = new StaticSamplerDescription(ShaderVisibility.All, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        var wrapSamp = new StaticSamplerDescription(ShaderVisibility.All, 1, 0) {   // albedo texture sampling at hits
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        rtReflRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(
                RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
                new[] { cbv0, cbv1, cbv2, table, matSrv, instSrv, lightSrv, probeSrv, cardSrv, metaSrv, triClusterSrv }, new[] { clampSamp, wrapSamp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("DxrReflections.hlsl");
        byte[] dxil = Dx12ShaderCompiler.Compile(DxcShaderStage.Library, hlsl, "", "DxrReflections.hlsl");
        var subs = new[] {
            new StateSubObject(new DxilLibraryDescription(dxil,
                new ExportDescription("RayGen"), new ExportDescription("Miss"), new ExportDescription("ClosestHit"))),
            new StateSubObject(new HitGroupDescription("HitGroup", HitGroupType.Triangles, "", "ClosestHit", "")),
            new StateSubObject(new RaytracingShaderConfig(16, 8)),   // payload = float3 color + float roughness
            new StateSubObject(new RaytracingPipelineConfig(1)),
            new StateSubObject(new GlobalRootSignature(rtReflRootSig)),
        };
        rtReflPso = device5.CreateStateObject(new StateObjectDescription(StateObjectType.RaytracingPipeline, subs));

        using ID3D12StateObjectProperties props = rtReflPso.QueryInterface<ID3D12StateObjectProperties>();
        uint idSize = Vortice.Direct3D12.D3D12.ShaderIdentifierSizeInBytes;
        rtReflSbt = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(RtSbtSlot * 3), ResourceStates.GenericRead);
        byte* sp = rtReflSbt.Map<byte>(0);
        System.Runtime.CompilerServices.Unsafe.CopyBlock(sp + 0 * RtSbtSlot, (void*)props.GetShaderIdentifier("RayGen"), idSize);
        System.Runtime.CompilerServices.Unsafe.CopyBlock(sp + 1 * RtSbtSlot, (void*)props.GetShaderIdentifier("Miss"), idSize);
        System.Runtime.CompilerServices.Unsafe.CopyBlock(sp + 2 * RtSbtSlot, (void*)props.GetShaderIdentifier("HitGroup"), idSize);
        rtReflSbt.Unmap(0);

        rtReflCb = new Dx12FrameCb<RtReflConstants>(dev);
        rtReflSunCb = new Dx12FrameCb<RtGiSun>(dev);
        rtReflGridCb = new Dx12FrameCb<RtReflGridConstants>(dev);
        _ = ctx.Dxr.RtGeometry;   // was `rtGeometry ??= new Dx12RtGeometry(dev)` — reuse if GI already built it.
        return true;
    }

    // RT reflections: trace a reflection ray per pixel → ssrTarget (reflected color + strength), then reuse the
    // SSR combine. PHASE 8: the hit is shaded with REAL world radiance (sun + punctual + the DDGI world-cache
    // field as ambient), so this needs the bindless geo/material table + the DDGI atlas/grid/ProbeState — bound
    // EXACTLY like DrawRtGi (the renderer is fully synchronous, so the DDGI atlas the GI pass wrote this frame
    // is fully drained before the reflection pass reads it).
    unsafe void DrawRtReflections(Dx12FrameContext ctx) {
        var dev = ctx.Dev; var target = ctx.SceneColor; var gbuffer = ctx.GBuffer; var ibl = ctx.Ibl;
        var gpuDriven = ctx.GpuDriven; var clusteredLights = ctx.ClusteredLights;
        var sceneAS = ctx.Dxr.SceneAS; var rtGeometry = ctx.Dxr.RtGeometry;
        Matrix4x4 view = ctx.View, viewProj = ctx.ViewProj, proj = ctx.Proj;
        Vector3 camPos = ctx.CamPos, lightDir = ctx.LightDir, lightColor = ctx.LightColor;

        sceneAS.Ensure(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection);
        if (!sceneAS.Valid) { DrawSsr(ctx); return; }   // no geometry → fall back to SSR

        // The world-radiance hit shading reads the bindless material table + per-instance geometry SRVs (same as
        // RT-GI) — ensure they're fresh (stamp-cached no-ops if the geometry pass already built them).
        gpuDriven.EnsureMaterialTable(ctx.WholeMeshRenderers);
        rtGeometry.Ensure(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection, gpuDriven);

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Matrix4x4.Invert(viewProj, out Matrix4x4 invVP);
        // P5: sample the Lumen card cache at reflection hits when Lumen is active this frame + has a valid cache
        // (so reflections see the same multi-bounce GI the diffuse does). Off → the hit re-shades direct+IBL.
        bool useCards = ctx.LumenActiveThisFrame && ctx.LumenScene is { Valid: true } && ctx.PostFX.LumenReflections
                        && ReflCardsAllowed;
        rtReflCb.Write(new RtReflConstants {
            InvViewProj = Matrix4x4.Transpose(invVP), CameraPos = camPos, Intensity = ForcedIntensity(ctx),
            PrefilterMaxMip = ibl != null ? ibl.PrefilterMipCount - 1 : 0f, NormalBias = 0.05f,
            UseCards = useCards ? 1f : 0f,
            Unused1 = 0f,
        });
        Vector3 sunDir = lightDir.LengthSquared() < 1e-8f ? Vector3.UnitY : Vector3.Normalize(lightDir);
        rtReflSunCb.Write(new RtGiSun {
            SunDir = sunDir, NormalBias = 0.03f, SunColor = lightColor, LightCount = clusteredLights.LightCount,
        });
        rtReflGridCb.Write(default);

        // The G-buffer is in the combined shader-read state; color (target) bring to SRV for the combine.
        target.ColorToShaderResource();
        // The DXR raygen samples depth (t1) from the NON-PIXEL stage — promote it (fog/SSGI leave depth in
        // PixelShaderResource only). The combine's back-half re-transitions depth (DepthToShaderResource below).
        gbuffer.DepthToNonPixelShaderResource();

        // The table descriptors live in the bindless heap's reserved tail (so the one bound CBV/SRV/UAV heap
        // serves BOTH the table AND the closest-hit's ResourceDescriptorHeap[] bindless geo/material reads).
        Dx12DescriptorHeap bindless = Dx12Backend.BindlessHeap;
        sceneAS.CreateTlasSrv(bindless.Cpu(RtReflTableBase + 0));                                            // t0 TLAS
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(RtReflTableBase + 1), gbuffer.DepthSrvCpu, heapType);     // t1 depth
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(RtReflTableBase + 2), gbuffer.ColorSrvCpu(1), heapType);  // t2 world normal
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(RtReflTableBase + 3), gbuffer.ColorSrvCpu(2), heapType);  // t3 material
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(RtReflTableBase + 4), ibl.IrradianceSrv, heapType);       // t4 irr cube
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(RtReflTableBase + 5), ibl.PrefilterSrv, heapType);        // t5 prefilter cube
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(RtReflTableBase + 6), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CreateUnorderedAccessView(ssrTarget.RenderTarget, null, new UnorderedAccessViewDescription {
            Format = Dx12OffscreenTarget.HdrFormat, ViewDimension = UnorderedAccessViewDimension.Texture2D,
        }, bindless.Cpu(RtReflTableBase + 7));                                                               // u0 ssrTarget

        ssrTarget.ColorToUnorderedAccess();
        uint idSize = Vortice.Direct3D12.D3D12.ShaderIdentifierSizeInBytes;
        dev.ExecuteSync(cl => {
            cl.SetDescriptorHeaps(bindless.Heap);   // bindless heap = the bound CBV/SRV/UAV heap (table + ResourceDescriptorHeap[])
            cl.SetComputeRootSignature(rtReflRootSig);
            cl.SetPipelineState1(rtReflPso);
            cl.SetComputeRootConstantBufferView(0, rtReflCb.Gpu);
            cl.SetComputeRootConstantBufferView(1, rtReflSunCb.Gpu);
            cl.SetComputeRootConstantBufferView(2, rtReflGridCb.Gpu);
            cl.SetComputeRootDescriptorTable(3, bindless.Gpu(RtReflTableBase));
            cl.SetComputeRootShaderResourceView(4, gpuDriven.MaterialsGpuAddress);       // t7 GpuMaterials
            cl.SetComputeRootShaderResourceView(5, rtGeometry.InstancesGpuAddress);      // t8 RtInstance[]
            cl.SetComputeRootShaderResourceView(6, clusteredLights.LightBufGpuAddress);  // t9 punctual lights
            cl.SetComputeRootShaderResourceView(7, clusteredLights.LightBufGpuAddress);  // t10 (probe, unused → filler)
            // P5: the Lumen card cache (this frame's lit + multi-bounce radiance, post-swap) + per-instance meta.
            // When Lumen is off, bind valid filler (the light buffer) — UseCards=0 gates the shader read anyway.
            ulong cardAddr = useCards ? ctx.LumenScene.CardRadianceReadGpu : clusteredLights.LightBufGpuAddress;
            ulong metaAddr = useCards ? ctx.LumenScene.InstanceMetaGpuAddress : clusteredLights.LightBufGpuAddress;
            ulong triClusAddr = useCards && ctx.LumenScene.TriToClusterGpuAddress != 0 ? ctx.LumenScene.TriToClusterGpuAddress : clusteredLights.LightBufGpuAddress;
            cl.SetComputeRootShaderResourceView(8, cardAddr);                            // t11 CardRadiance
            cl.SetComputeRootShaderResourceView(9, metaAddr);                            // t12 LumenInstanceMeta
            cl.SetComputeRootShaderResourceView(10, triClusAddr);                         // t13 TriToCluster (#2A)
            cl.DispatchRays(new DispatchRaysDescription {
                Width = (uint)ssrTarget.Width, Height = (uint)ssrTarget.Height, Depth = 1,
                RayGenerationShaderRecord = new GpuVirtualAddressRange { StartAddress = rtReflSbt.GPUVirtualAddress, SizeInBytes = idSize },
                MissShaderTable = new GpuVirtualAddressRangeAndStride { StartAddress = rtReflSbt.GPUVirtualAddress + RtSbtSlot, SizeInBytes = idSize, StrideInBytes = idSize },
                HitGroupTable = new GpuVirtualAddressRangeAndStride { StartAddress = rtReflSbt.GPUVirtualAddress + 2 * RtSbtSlot, SizeInBytes = idSize, StrideInBytes = idSize },
            });
        });
        ssrTarget.ColorToShaderResource();

        // Temporal-denoise the half-res RT reflection (kills the cache-churn mirror jitter) — shared with SSR.
        // BALLISTIC_DX12_REFL_NOTEMPORAL=1 bypasses it (see DrawSsr) when the denoise erases moving reflections.
        Dx12OffscreenTarget reflForCombine = ReflTemporalEnabled
            ? DenoiseReflectionTemporal(ctx, gbuffer)
            : ssrTarget;

        // Reuse the SSR combine (depth-aware upsample + Fresnel lerp into the scene color).
        Matrix4x4.Invert(proj, out Matrix4x4 invProj);
        *(SsrConstants*)ssrCbMapped = new SsrConstants {
            Projection = Matrix4x4.Transpose(proj), InvProjection = Matrix4x4.Transpose(invProj),
            ViewMatrix = Matrix4x4.Transpose(view), Intensity = ForcedIntensity(ctx),
            TexelSize = new Vector2(1f / ssrTarget.Width, 1f / ssrTarget.Height),
        };
        gbuffer.DepthToShaderResource();
        ssrSrvVisible.Reset();
        int cb = ssrSrvVisible.AllocateRange(5);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 0), target.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 1), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 2), gbuffer.ColorSrvCpu(1), heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 3), gbuffer.ColorSrvCpu(2), heapType);
        dev.Device.CopyDescriptorsSimple(1, ssrSrvVisible.Cpu(cb + 4), reflForCombine.ColorSrvCpu, heapType);   // DENOISED reflection
        ssrScene.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(ssrRootSig); cl.SetPipelineState(ssrCombinePso);
            cl.SetDescriptorHeaps(ssrSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, ssrCb.GPUVirtualAddress);
            cl.SetGraphicsRootDescriptorTable(1, ssrSrvVisible.Gpu(cb));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
        ssrScene.ColorToShaderResource();
        target.CopyColorFrom(ssrScene);
    }

    // ============================== build + helpers ==============================

    // BuildSsr moved VERBATIM. The march + combine PSOs share one rootsig + CB; the half-res reflection target +
    // full-res combine scratch allocate in Resize.
    unsafe void BuildSsr() {
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        // V2 NOTE: DataVolatile (not the default DataStaticWhileSetAtExecute) on the SRV range. The SSR combine
        // samples ssrTarget, which under BALLISTIC_DX12_GRAPH_ALIAS=1 is a PLACED resource whose backing memory is
        // later aliased to another target — that breaks the DATA_STATIC "this resource's state won't change after
        // SetDescriptorTable" promise the default flag makes (GBV flagged it: InvalidSubresourceState "(assumed at
        // first use)" on the aliased ssrTarget). DataVolatile is the spec-correct flag for a descriptor whose
        // resource may be aliased; it only RELAXES a driver caching assumption → pixel-neutral on BOTH paths
        // (default/V1 still SHA==golden, verified). Applies to all 5 SRVs in the range (uniform; only ssrTarget is
        // aliased, the G-buffer SRVs are committed, but DataVolatile is harmless for them too).
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 5, baseShaderRegister: 0,
            registerSpace: 0, offsetInDescriptorsFromTableStart: 0, flags: DescriptorRangeFlags.DataVolatile);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var samp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        ssrRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { samp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("Ssr.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "Ssr.hlsl");
        ID3D12PipelineState MakePso(string entry, Format rtFmt) => dev.Device.CreateGraphicsPipelineState(
            new GraphicsPipelineStateDescription {
                RootSignature = ssrRootSig, VertexShader = vs,
                PixelShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, entry, "Ssr.hlsl"),
                InputLayout = null, PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
                RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
                DepthStencilState = DepthStencilDescription.None,
                RenderTargetFormats = new[] { rtFmt }, DepthStencilFormat = Format.Unknown,
                SampleDescription = new SampleDescription(1, 0),
            });
        ssrMarchPso = MakePso("PSMarch", Dx12OffscreenTarget.HdrFormat);
        ssrCombinePso = MakePso("PSCombine", Dx12OffscreenTarget.HdrFormat);
        ssrTemporalPso = MakePso("PSTemporal", Dx12OffscreenTarget.HdrFormat);   // RT-reflection temporal denoise

        int cbSize = (Marshal.SizeOf<SsrConstants>() + 255) & ~255;
        ssrCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        ssrCbMapped = ssrCb.Map<byte>(0);
        ssrSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 10, shaderVisible: true, framesInFlight: dev.FramesInFlight);
    }

    // AllocSsrTarget moved VERBATIM into Resize (graph.Resize fans this out in registration order, R5).
    public void Resize(int w, int h) {
        // V2: ssrTarget/ssrScene are audit-passed transients (the SSR march/RT dispatch fully writes ssrTarget
        // before the combine reads it; the combine fully overwrites ssrScene). AllocOrPool = committed when no pool
        // (byte-identical), placed-aliased when the pool is active. ssrTarget allowUav (RT reflections write it via
        // a UAV; SSR via the RTV) — the placed heap is RT/DS-flagged + AllowUnorderedAccess on the resource desc.
        // Dispose the current field unless it's pool-placed (the pool's re-acquire disposes its own Live).
        if (ssrTarget is { IsPlaced: false }) ssrTarget.Dispose();
        if (ssrScene is { IsPlaced: false }) ssrScene.Dispose();
        ssrHistoryA?.Dispose(); ssrHistoryB?.Dispose();
        int hw = Math.Max(1, w / 2), hh = Math.Max(1, h / 2);
        ssrTarget = Dx12RenderTargetPool.AllocOrPool(dev, "ssrTarget", hw, hh, Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: true);
        ssrScene = Dx12RenderTargetPool.AllocOrPool(dev, "ssrScene", w, h, Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: false);
        // RT-reflection temporal history (half-res, committed — cross-frame, never pooled/aliased).
        ssrHistoryA = new Dx12OffscreenTarget(dev, hw, hh, withDepth: false, colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
        ssrHistoryB = new Dx12OffscreenTarget(dev, hw, hh, withDepth: false, colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
        ssrHistValid = false;
    }

    public void Dispose() {
        ssrMarchPso?.Dispose(); ssrCombinePso?.Dispose(); ssrTemporalPso?.Dispose();
        ssrRootSig?.Dispose(); ssrCb?.Dispose(); ssrSrvVisible?.Dispose();
        ssrTarget?.Dispose(); ssrScene?.Dispose();
        ssrHistoryA?.Dispose(); ssrHistoryB?.Dispose();
        rtReflPso?.Dispose(); rtReflRootSig?.Dispose();
        rtReflSbt?.Dispose();
        rtReflCb?.Dispose(); rtReflSunCb?.Dispose(); rtReflGridCb?.Dispose();   // Dx12FrameCb.Dispose unmaps + releases
    }
}
