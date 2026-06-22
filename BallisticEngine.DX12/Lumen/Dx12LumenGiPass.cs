using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;          // PrimitiveTopology
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// Lumen (UE5-style) GI — the future product GI path being ported alongside Aurora. Event = GlobalIllumination
// (500), the SAME slot Aurora occupies (after Transparents, before Fog) — the two are MUTUALLY EXCLUSIVE (only
// one GI pass runs per frame; see the arbitration note below).
//
// FAZ 0 (THIS milestone — scaffold only): wire the subsystem in cleanly with correct door + gating, but produce
// NOTHING visible. Record() builds/updates the Dx12LumenScene substrate (shared TLAS + per-instance meta + dirty
// stamps) and does nothing else — it writes NO GI, so the scene renders with direct lighting + IBL only and GI is
// black. That is CORRECT for FAZ 0. Later phases fill in the real pipeline:
//   - FAZ 1: per-mesh SDF + software ray tracing.
//   - FAZ 2/3: mesh cards + the surface cache (lit, view-independent radiance).
//   - FAZ 6: screen-probe GI — the first phase that actually CONTRIBUTES diffuse indirect; at THAT point the
//     deferred pass must suppress its IBL diffuse ambient when Lumen is active (see the // FAZ 6 marker in
//     Dx12DeferredLightingPass.Record), exactly as it already does for Aurora, to avoid double-counting.
//
// Gated behind BALLISTIC_DX12_LUMEN (env, default off). When armed, Lumen takes PRECEDENCE over Aurora: Aurora's
// WouldRun yields (it checks !Dx12LumenGiPass.Armed(ctx)), so BALLISTIC_DX12_LUMEN=1 disables Aurora and runs the
// (no-op) Lumen pass instead. HW-RT only (same gate as Aurora — no software fallback in FAZ 0).
public sealed class Dx12LumenGiPass : IRenderPass, IDisposable
{
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.GlobalIllumination;
    public string Name => "Lumen GI";

    readonly Dx12Device dev;
    readonly Dx12LumenScene scene;

    // FAZ 2: the runtime GLOBAL DISTANCE FIELD (camera-centered clipmap composited from per-mesh SDFs). Built each
    // frame when the GlobalSdf door is armed (BALLISTIC_DX12_GLOBALSDF=1, or implicitly when Lumen is on). Feeds GI
    // in FAZ 5; FAZ 2 only builds + (optionally) sphere-trace-visualizes it. Lazily constructed on first armed frame.
    Dx12GlobalSdf globalSdf;

    // FAZ 2 debug view (BALLISTIC_DX12_GLOBALSDF_DEBUG=1): a fullscreen sphere-trace of the clipmap, opaque-replace
    // into the HDR scene color, so the field's correctness is VISIBLE. Lazily built with the SDF.
    ID3D12RootSignature dbgRootSig;
    ID3D12PipelineState dbgPso;
    Dx12FrameCb<GlobalSdfDebugConstants> dbgCb;
    Dx12DescriptorHeap dbgSrv;   // 1 SRV (clipmap) per frame

    [StructLayout(LayoutKind.Sequential)]
    struct GlobalSdfDebugConstants {
        public Matrix4x4 InvViewProj;
        public Vector3 CamPos;        public float VoxelSize;
        public Vector3 ClipOrigin;    public float ClipHalfExtent;
        public uint ClipResX, ClipResY, ClipResZ; public float MaxTraceDist;
        public Vector3 KeyLightDir;   public float HitEpsilon;
    }

    static int sdfDoor = -2;       // -2 unread, 0 off, 1 on (build the field)
    static int sdfDebugDoor = -2;  // -2 unread, 0 off, 1 on (sphere-trace debug view)
    static bool SdfArmed(Dx12FrameContext ctx) {
        if (sdfDoor == -2)
            sdfDoor = Environment.GetEnvironmentVariable("BALLISTIC_DX12_GLOBALSDF") == "1" ? 1 : 0;
        // Build the field when its own door is set OR Lumen is armed (the field is part of the Lumen substrate).
        return sdfDoor == 1 || Armed(ctx);
    }
    static bool SdfDebug() {
        if (sdfDebugDoor == -2)
            sdfDebugDoor = Environment.GetEnvironmentVariable("BALLISTIC_DX12_GLOBALSDF_DEBUG") == "1" ? 1 : 0;
        return sdfDebugDoor == 1;
    }

    // The Lumen scene substrate (shared TLAS + per-instance meta). Exposed read-only for later phases (e.g. the
    // reflections pass sampling the surface cache, once FAZ 2/3 land); valid only after a successful Ensure.
    public Dx12LumenScene Scene => scene;

    // FAZ 2: the global distance field (null until first armed frame). Exposed read-only for FAZ 5 (SDF software RT).
    public Dx12GlobalSdf GlobalSdf => globalSdf;

    public Dx12LumenGiPass(Dx12Device device, int width, int height)
    {
        dev = device;
        scene = new Dx12LumenScene(device);
        // FAZ 0/2: GI pipelines are not built here (no GI is written yet). The global SDF + its debug pipeline are
        // lazily built on the first frame the SDF door is armed (so default-off allocates nothing).
    }

    unsafe void EnsureSdfDebugPipeline()
    {
        if (dbgPso != null) return;
        // Fullscreen sphere-trace: CBV b0 + 1-SRV table (clipmap t0) + clamp sampler s0. Opaque replace into HDR.
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var clamp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1, ComparisonFunction = ComparisonFunction.Never,
            MinLOD = 0, MaxLOD = float.MaxValue,
        };
        dbgRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { clamp })));

        string hlsl = EmbeddedShaderSource.ReadHlsl("Lumen/GlobalSdfDebug.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSDebug", "GlobalSdfDebug.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSDebug", "GlobalSdfDebug.hlsl");
        dbgPso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = dbgRootSig, VertexShader = vs, PixelShader = ps, InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat },
            DepthStencilFormat = Format.Unknown, SampleDescription = new SampleDescription(1, 0),
        });
        dbgCb = new Dx12FrameCb<GlobalSdfDebugConstants>(dev);
        dbgSrv = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, shaderVisible: true,
            framesInFlight: dev.FramesInFlight);
    }

    unsafe void RecordSdfDebug(Dx12FrameContext ctx)
    {
        EnsureSdfDebugPipeline();
        globalSdf.ToPixelShaderResource();

        Matrix4x4.Invert(ctx.ViewProj, out Matrix4x4 invVP);
        Vector3 keyLight = ctx.LightDir.LengthSquared() < 1e-8f ? new Vector3(-0.4f, -1f, -0.3f) : ctx.LightDir;
        dbgCb.Write(new GlobalSdfDebugConstants {
            InvViewProj = Matrix4x4.Transpose(invVP),
            CamPos = ctx.CamPos, VoxelSize = globalSdf.ClipVoxelSize,
            ClipOrigin = globalSdf.ClipOrigin, ClipHalfExtent = globalSdf.ClipHalf,
            ClipResX = (uint)globalSdf.ClipRes, ClipResY = (uint)globalSdf.ClipRes, ClipResZ = (uint)globalSdf.ClipRes,
            MaxTraceDist = globalSdf.ClipWorldExtent * 1.8f,
            KeyLightDir = Vector3.Normalize(keyLight), HitEpsilon = globalSdf.ClipVoxelSize * 0.5f,
        });

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        dbgSrv.Reset();
        int b = dbgSrv.AllocateRange(1);
        dev.Device.CopyDescriptorsSimple(1, dbgSrv.Cpu(b), globalSdf.ClipmapSrvCpu, heapType);

        ulong cbAddr = dbgCb.Gpu;
        GpuDescriptorHandle srvGpu = dbgSrv.Gpu(b);
        ID3D12DescriptorHeap heap = dbgSrv.Heap;
        ctx.SceneColor.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(dbgRootSig);
            cl.SetPipelineState(dbgPso);
            cl.SetDescriptorHeaps(heap);
            cl.SetGraphicsRootConstantBufferView(0, cbAddr);
            cl.SetGraphicsRootDescriptorTable(1, srvGpu);
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
    }

    // The product door. FAZ 0 is ENV-ONLY (BALLISTIC_DX12_LUMEN=1) — there is no LumenVolume yet.
    // TODO (later phase): add a LumenVolume (mirroring AuroraVolume) and follow it when the env is unset, just like
    // Aurora's Armed() folds in ctx.PostFX.AuroraEnabled. For now: armed iff the env door is "1".
    static int envDoor = -2;   // -2 unread, -1 unset (off), 0 force-off, 1 force-on
    public static bool Armed(Dx12FrameContext ctx)
    {
        if (envDoor == -2)
        {
            string v = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN");
            envDoor = v == "1" ? 1 : v == "0" ? 0 : -1;
        }
        // FAZ 0: env-only. No volume fallback yet → unset (-1) and force-off (0) both mean OFF.
        return envDoor == 1;
    }

    // FAZ 2: "the Lumen PASS should run this frame" — Lumen GI armed OR the global-SDF door is set on its own (so
    // the field can be built + debugged independently of the full Lumen GI, per the FAZ 2 brief). Aurora yields to
    // this predicate (not just Armed) so a GLOBALSDF-only run replaces Aurora at event 500 and the debug view isn't
    // overpainted. Env-frame-independent (no ctx state read beyond the doors), safe for Aurora's static yield check.
    public static bool ScenePathArmed(Dx12FrameContext ctx) => Armed(ctx) || SdfArmed(ctx);

    // The frame-level "Lumen runs" predicate, shared with the orchestrator (mirrored into ctx.LumenActiveThisFrame
    // in BeginRender). Same hard gates as Aurora — HW-RT only, valid TLAS, not the Minimal door. Lumen takes
    // precedence over Aurora purely via ScenePathArmed: when Lumen (or the SDF door) is armed, Aurora.WouldRun
    // returns false (it checks !Dx12LumenGiPass.ScenePathArmed), so only one GI pass is Enabled per frame.
    public static bool WouldRun(Dx12FrameContext ctx) =>
        !ctx.Doors.Minimal && ScenePathArmed(ctx) && ctx.Dev.HasHardwareRayTracing && ctx.Dxr?.SceneAS != null;

    // FAZ -1d-FINAL — when render-graph v2 owns the whole frame (v1 bypassed) it drives the GI slot itself; the v1
    // graph then SKIPS this pass via RgV2OwnsLumenGi. Gate ONLY the instance Enabled, NOT the static WouldRun (read
    // elsewhere to mirror ctx.LumenActiveThisFrame), exactly like Aurora. Door off (and door-on-while-plumbing) =>
    // the flag is false => Enabled == WouldRun, unchanged. See Dx12FrameContext.RgV2OwnsLumenGi.
    public bool Enabled(Dx12FrameContext ctx) => WouldRun(ctx) && !ctx.RgV2OwnsLumenGi;

    // FAZ -1d-FINAL — render-graph v2 entry point. FAZ 0 Record only builds/refreshes the Lumen scene substrate and
    // writes NO GI (scene color is untouched), so RecordV2 needs NO input-state forcing — there is nothing it reads
    // that the v2 import barriers must satisfy. Just run the same body. When FAZ 6 adds real screen-probe GI output
    // (reading G-buffer depth/normal + writing scene color), force those entry states here, mirroring
    // Dx12AuroraGiPass.RecordV2.
    public void RecordV2(Dx12FrameContext ctx) => Record(ctx);

    public void Record(Dx12FrameContext ctx)
    {
        // FAZ 0: build/refresh the scene substrate ONLY. No GI is traced or combined → scene color is untouched,
        // GI stays black. The first armed frame logs the substrate counts (Dx12LumenScene.Ensure logs once per
        // stamp). Later phases trace SDF/screen probes here and additively combine indirect into the HDR color.
        if (!scene.Ensure(ctx))
            return;   // no valid scene AS → nothing to build (Lumen is HW-RT only in FAZ 0; no software fallback)

        // FAZ 2: build/refresh the camera-centered GLOBAL DISTANCE FIELD clipmap from the visible meshes' per-mesh
        // SDFs. Armed by BALLISTIC_DX12_GLOBALSDF=1 (independent test) OR whenever Lumen GI is on (the field is part
        // of the Lumen substrate). Builds NOTHING into the scene color by itself — FAZ 5 sphere-marches it for GI.
        if (SdfArmed(ctx))
        {
            globalSdf ??= new Dx12GlobalSdf(dev);
            globalSdf.Build(ctx, ctx.Dxr.SceneAS, scene.DirtyThisFrame);

            // FAZ 2 verification artifact: sphere-trace the field per screen pixel into the HDR scene color so the
            // silhouette/surfaces appear where geometry is. Default off → scene color untouched. Guarded on a valid
            // (built) field so a scene with no SDFs (pre-v8 meshes) doesn't crash — just shows the empty-field bg.
            if (SdfDebug() && globalSdf.Valid && ctx.SceneColor != null)
                RecordSdfDebug(ctx);
        }

        // TODO FAZ 5+: SDF software ray trace (sphere-march this clipmap) → FAZ 3: surface-cache gather → FAZ 6:
        // screen-probe diffuse + additive combine.
    }

    public void Dispose()
    {
        scene?.Dispose();
        globalSdf?.Dispose();
        dbgPso?.Dispose(); dbgRootSig?.Dispose(); dbgCb?.Dispose(); dbgSrv?.Dispose();
    }
}
