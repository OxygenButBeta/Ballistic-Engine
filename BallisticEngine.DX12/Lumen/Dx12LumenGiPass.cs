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

    // FAZ 6: the SCREEN-PROBE GATHER — the first VISIBLE integrated Lumen GI. Places sparse screen probes, traces
    // them via LumenTrace (sampling the LIT surface cache), integrates per-pixel diffuse irradiance E, and ADDS it
    // to the scene color. Lazily constructed; runs only when Lumen GI is armed (BALLISTIC_DX12_LUMEN=1). The
    // deferred pass suppresses its IBL diffuse ambient when ctx.LumenActiveThisFrame so this doesn't double-count.
    Dx12LumenScreenProbe screenProbe;

    // FAZ 7: the WORLD-SPACE RADIANCE CACHE — a sparse, persistent, camera-centered clipmap of octahedral world-space
    // radiance probes. The far-field GI noise reducer: the screen probes trace SHORT rays and, on a miss within the
    // cell's space-diagonal trace-stop, MARK the covering cell + SAMPLE the cache for distant radiance. 1-frame
    // deferred: this builds (allocate+trace+fixup) the cells the screen probes marked LAST frame BEFORE the
    // screen-probe gather, which then samples the (now-filled) cache + marks for NEXT frame. Gated on the RC door
    // (BALLISTIC_DX12_LUMEN_RC, default ON when Lumen on; =0 → screen probe traces full distance = FAZ 6 fallback).
    Dx12LumenRadianceCache radianceCache;
    static int rcDoor = -2;   // -2 unread, 0 off, 1 on
    static bool RcArmed() {
        if (rcDoor == -2)
            rcDoor = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_RC") == "0" ? 0 : 1;
        return rcDoor == 1;
    }

    // FAZ 8: LUMEN REFLECTIONS — the specular reflection ray resolved through the SAME LumenTrace abstraction the
    // diffuse screen-probe gather uses (HW TLAS / SW SDF → the LIT surface cache FinalLighting), so a reflective
    // surface mirrors the lit walls with the cache's GI color bleed (no re-shading). Runs at event 600 timing (after
    // the GI combine at 500). Gated on the REFL door (BALLISTIC_DX12_LUMEN_REFL, default ON when Lumen on; =0 →
    // the existing Dx12ReflectionsPass runs instead). When ON, that existing pass yields (its WouldRun checks
    // !ReflectionsActive) so reflections are never double-composited.
    Dx12LumenReflections reflections;
    static int reflDoor = -2;   // -2 unread, 0 off, 1 on
    static bool ReflArmed() {
        if (reflDoor == -2)
            reflDoor = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_REFL") == "0" ? 0 : 1;
        return reflDoor == 1;
    }

    // The frame-level "Lumen reflections own the specular reflection this frame" predicate, read by
    // Dx12ReflectionsPass.WouldRun so the existing reflections pass YIELDS (mutual exclusion — never two reflection
    // composites). True iff the Lumen GI scene path runs AND the REFL door is on. Env-frame-independent (doors only),
    // safe for the static yield check.
    public static bool ReflectionsActive(Dx12FrameContext ctx) => WouldRun(ctx) && ReflArmed();

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

    // FAZ 3b debug view (BALLISTIC_DX12_LUMEN_CARDS_DEBUG=1): a fullscreen ray-test of the world-space card OBBs,
    // opaque-replace into the HDR scene color, each hit shaded by its DirectionIndex color so the card PLACEMENT +
    // ORIENTATION is VISIBLE. Mirrors the SDF debug pipeline exactly (CBV b0 + root SRV t0 cards; reconstruct the
    // view ray from InvViewProj). Lazily built on the first debug frame.
    ID3D12RootSignature cardDbgRootSig;
    ID3D12PipelineState cardDbgPso;
    Dx12FrameCb<CardDebugConstants> cardDbgCb;

    [StructLayout(LayoutKind.Sequential)]
    struct CardDebugConstants {
        public Matrix4x4 InvViewProj;   // clip → world (transposed on upload)
        public Vector3 CamPos;          public uint CardCount;
        public float MaxTraceDist;      public Vector3 CardDbgPad;
    }

    bool loggedCardDbg;
    static int cardDebugDoor = -2;  // -2 unread, 0 off, 1 on (card OBB debug view)
    static bool CardDebug() {
        if (cardDebugDoor == -2)
            cardDebugDoor = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_CARDS_DEBUG") == "1" ? 1 : 0;
        return cardDebugDoor == 1;
    }

    // FAZ 3c — capture debug blit (BALLISTIC_DX12_LUMEN_CAPTURE_DEBUG=1): blit one surface-cache atlas to scene color
    // so the captured attributes are visible. The atlas is selected by BALLISTIC_DX12_LUMEN_CAPTURE_VIEW.
    ID3D12RootSignature capDbgRootSig;
    ID3D12PipelineState capDbgPso;
    Dx12FrameCb<CaptureDebugConstants> capDbgCb;
    Dx12DescriptorHeap capDbgSrv;
    bool loggedCapDbg;

    [StructLayout(LayoutKind.Sequential)]
    struct CaptureDebugConstants { public uint Mode; public float Scale; public Vector2 Pad; }

    static int capDebugDoor = -2;  // -2 unread, 0 off, 1 on (capture atlas blit)
    static bool CaptureDebug() {
        if (capDebugDoor == -2)
            capDebugDoor = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_CAPTURE_DEBUG") == "1" ? 1 : 0;
        return capDebugDoor == 1;
    }
    // Which atlas to blit + a per-view visualization gain. Albedo ~1.5, normal/depth pushed brighter (Scale) so they
    // survive the post tonemap; emissive shown at low gain (it's already HDR).
    static void CaptureViewMode(out uint mode, out float scale) {
        string v = (Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_CAPTURE_VIEW") ?? "albedo").ToLowerInvariant();
        switch (v) {
            case "normal":   mode = 1; scale = 1.0f; break;
            case "emissive": mode = 2; scale = 1.0f; break;
            case "depth":    mode = 3; scale = 1.0f; break;
            default:         mode = 0; scale = 1.5f; break;   // albedo
        }
    }

    // FAZ 3d — surface-cache LIGHTING gate. Lit whenever the cards are armed (Lumen GI on OR the card door set), so
    // a cards-only test run (BALLISTIC_DX12_LUMEN_CARDS=1) still lights the cache. Off only if explicitly disabled.
    static int lightDoor = -2;   // -2 unread, 0 off, 1 on
    static bool LightArmed(Dx12FrameContext ctx) {
        if (lightDoor == -2)
            lightDoor = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_NOLIGHT") == "1" ? 0 : 1;
        return lightDoor == 1 && (Armed(ctx) || CardsDoorOn());
    }
    static int cardsDoor = -2;
    static bool CardsDoorOn() {
        if (cardsDoor == -2)
            cardsDoor = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_CARDS") == "1" ? 1 : 0;
        return cardsDoor == 1;
    }

    // FAZ 3d — lit-cache debug blit (BALLISTIC_DX12_LUMEN_LIGHT_DEBUG=1): blit FinalLighting / DirectLighting to scene
    // color so the LIT surface cache is visible (selectable via BALLISTIC_DX12_LUMEN_LIGHT_VIEW=final|direct).
    ID3D12RootSignature litDbgRootSig;
    ID3D12PipelineState litDbgPso;
    Dx12FrameCb<CaptureDebugConstants> litDbgCb;
    Dx12DescriptorHeap litDbgSrv;
    bool loggedLitDbg;
    static int litDebugDoor = -2;
    static bool LightDebug() {
        if (litDebugDoor == -2)
            litDebugDoor = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_LIGHT_DEBUG") == "1" ? 1 : 0;
        return litDebugDoor == 1;
    }
    static void LightViewMode(out uint mode, out float scale) {
        string v = (Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_LIGHT_VIEW") ?? "final").ToLowerInvariant();
        switch (v) {
            case "direct": mode = 1; scale = 1.0f; break;
            default:       mode = 0; scale = 1.0f; break;   // final
        }
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

    unsafe void EnsureCardDebugPipeline()
    {
        if (cardDbgPso != null) return;
        // Fullscreen card-OBB ray-test: CBV b0 + root SRV t0 (GpuLumenCard[]). Opaque replace into HDR. No sampler
        // (the card test is analytic — no texture reads). Mirrors the SDF debug root sig minus the SRV table/sampler.
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var cardSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.Pixel);
        cardDbgRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, cardSrv }, Array.Empty<StaticSamplerDescription>())));

        string hlsl = EmbeddedShaderSource.ReadHlsl("Lumen/LumenCardDebug.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSDebug", "LumenCardDebug.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSDebug", "LumenCardDebug.hlsl");
        cardDbgPso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = cardDbgRootSig, VertexShader = vs, PixelShader = ps, InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat },
            DepthStencilFormat = Format.Unknown, SampleDescription = new SampleDescription(1, 0),
        });
        cardDbgCb = new Dx12FrameCb<CardDebugConstants>(dev);
    }

    unsafe void RecordCardDebug(Dx12FrameContext ctx)
    {
        Dx12LumenCardScene cards = scene.CardScene;
        if (cards is null || !cards.Valid || cards.CardBufferGpuAddress == 0) {
            if (!loggedCardDbg) { loggedCardDbg = true;
                Console.WriteLine($"[LumenCardsDebug] SKIP cards={(cards==null?"null":cards.CardCount.ToString())} valid={cards?.Valid} addr={cards?.CardBufferGpuAddress}"); }
            return;
        }
        if (!loggedCardDbg) { loggedCardDbg = true;
            Console.WriteLine($"[LumenCardsDebug] DRAW cards={cards.CardCount} camPos={ctx.CamPos}"); }
        EnsureCardDebugPipeline();

        Matrix4x4.Invert(ctx.ViewProj, out Matrix4x4 invVP);
        cardDbgCb.Write(new CardDebugConstants {
            InvViewProj = Matrix4x4.Transpose(invVP),
            CamPos = ctx.CamPos, CardCount = (uint)cards.CardCount,
            MaxTraceDist = 1e5f,
        });

        ulong cbAddr = cardDbgCb.Gpu;
        ulong cardAddr = cards.CardBufferGpuAddress;
        ctx.SceneColor.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(cardDbgRootSig);
            cl.SetPipelineState(cardDbgPso);
            cl.SetGraphicsRootConstantBufferView(0, cbAddr);
            cl.SetGraphicsRootShaderResourceView(1, cardAddr);
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
    }

    unsafe void EnsureCaptureDebugPipeline()
    {
        if (capDbgPso != null) return;
        // Fullscreen blit: CBV b0 + 1-SRV table (atlas t0) + point-clamp sampler s0. Opaque replace into HDR.
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var pointClamp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1, ComparisonFunction = ComparisonFunction.Never,
            MinLOD = 0, MaxLOD = float.MaxValue,
        };
        capDbgRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { pointClamp })));

        string hlsl = EmbeddedShaderSource.ReadHlsl("Lumen/LumenCardCaptureDebug.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSDebug", "LumenCardCaptureDebug.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSDebug", "LumenCardCaptureDebug.hlsl");
        capDbgPso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = capDbgRootSig, VertexShader = vs, PixelShader = ps, InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat },
            DepthStencilFormat = Format.Unknown, SampleDescription = new SampleDescription(1, 0),
        });
        capDbgCb = new Dx12FrameCb<CaptureDebugConstants>(dev);
        capDbgSrv = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, shaderVisible: true,
            framesInFlight: dev.FramesInFlight);
    }

    unsafe void RecordCaptureDebug(Dx12FrameContext ctx)
    {
        Dx12LumenCardScene cards = scene.CardScene;
        if (cards is null || !cards.Valid) {
            if (!loggedCapDbg) { loggedCapDbg = true;
                Console.WriteLine($"[LumenCaptureDebug] SKIP cards={(cards==null?"null":cards.CardCount.ToString())} valid={cards?.Valid}"); }
            return;
        }
        EnsureCaptureDebugPipeline();
        CaptureViewMode(out uint mode, out float scale);

        // Pick the selected atlas's CPU SRV (the atlas is in UnorderedAccess between passes; the SRV reads it directly
        // — UAV/SRV co-readable on the same persistent texture, the surface cache's steady state).
        CpuDescriptorHandle atlasSrv = mode switch {
            1 => cards.NormalSrvCpu,
            2 => cards.EmissiveSrvCpu,
            3 => cards.DepthSrvCpu,
            _ => cards.AlbedoSrvCpu,
        };
        if (!loggedCapDbg) { loggedCapDbg = true;
            Console.WriteLine($"[LumenCaptureDebug] DRAW mode={mode} scale={scale} captured={cards.Captured} cards={cards.CardCount}"); }

        capDbgCb.Write(new CaptureDebugConstants { Mode = mode, Scale = scale, Pad = Vector2.Zero });

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        capDbgSrv.Reset();
        int b = capDbgSrv.AllocateRange(1);
        dev.Device.CopyDescriptorsSimple(1, capDbgSrv.Cpu(b), atlasSrv, heapType);

        ulong cbAddr = capDbgCb.Gpu;
        GpuDescriptorHandle srvGpu = capDbgSrv.Gpu(b);
        ID3D12DescriptorHeap heap = capDbgSrv.Heap;
        ctx.SceneColor.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(capDbgRootSig);
            cl.SetPipelineState(capDbgPso);
            cl.SetDescriptorHeaps(heap);
            cl.SetGraphicsRootConstantBufferView(0, cbAddr);
            cl.SetGraphicsRootDescriptorTable(1, srvGpu);
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
    }

    unsafe void EnsureLightDebugPipeline()
    {
        if (litDbgPso != null) return;
        // Fullscreen blit: CBV b0 + 1-SRV table (atlas t0) + point-clamp sampler s0. Opaque replace into HDR.
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var pointClamp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1, ComparisonFunction = ComparisonFunction.Never,
            MinLOD = 0, MaxLOD = float.MaxValue,
        };
        litDbgRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { pointClamp })));

        string hlsl = EmbeddedShaderSource.ReadHlsl("Lumen/LumenCardLightDebug.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSDebug", "LumenCardLightDebug.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSDebug", "LumenCardLightDebug.hlsl");
        litDbgPso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = litDbgRootSig, VertexShader = vs, PixelShader = ps, InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat },
            DepthStencilFormat = Format.Unknown, SampleDescription = new SampleDescription(1, 0),
        });
        litDbgCb = new Dx12FrameCb<CaptureDebugConstants>(dev);
        litDbgSrv = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, shaderVisible: true,
            framesInFlight: dev.FramesInFlight);
    }

    unsafe void RecordLightDebug(Dx12FrameContext ctx)
    {
        Dx12LumenCardScene cards = scene.CardScene;
        if (cards is null || !cards.Valid) {
            if (!loggedLitDbg) { loggedLitDbg = true;
                Console.WriteLine($"[LumenLightDebug] SKIP cards={(cards==null?"null":cards.CardCount.ToString())} valid={cards?.Valid}"); }
            return;
        }
        EnsureLightDebugPipeline();
        LightViewMode(out uint mode, out float scale);
        scale *= EnvF("BALLISTIC_DX12_LUMEN_LIGHT_GAIN", 1f);

        // The selected lit atlas's CPU SRV (FinalLighting = last lit frame after the swap, or DirectLighting). Both
        // rest in UnorderedAccess between passes; the SRV reads the same persistent texture directly.
        CpuDescriptorHandle atlasSrv = mode switch {
            1 => cards.DirectSrvCpu,
            _ => cards.FinalSrvCpu,
        };
        if (!loggedLitDbg) { loggedLitDbg = true;
            Console.WriteLine($"[LumenLightDebug] DRAW mode={mode} scale={scale:0.##} finalValid={cards.FinalValid} cards={cards.CardCount}"); }

        litDbgCb.Write(new CaptureDebugConstants { Mode = mode, Scale = scale, Pad = Vector2.Zero });

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        litDbgSrv.Reset();
        int b = litDbgSrv.AllocateRange(1);
        dev.Device.CopyDescriptorsSimple(1, litDbgSrv.Cpu(b), atlasSrv, heapType);

        ulong cbAddr = litDbgCb.Gpu;
        GpuDescriptorHandle srvGpu = litDbgSrv.Gpu(b);
        ID3D12DescriptorHeap heap = litDbgSrv.Heap;
        ctx.SceneColor.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(litDbgRootSig);
            cl.SetPipelineState(litDbgPso);
            cl.SetDescriptorHeaps(heap);
            cl.SetGraphicsRootConstantBufferView(0, cbAddr);
            cl.SetGraphicsRootDescriptorTable(1, srvGpu);
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
    }

    // FAZ 5 — TRACE DEBUG view (BALLISTIC_DX12_LUMEN_TRACE_DEBUG=1): per camera pixel, gather N cosine hemisphere rays
    // through the shared LumenTrace abstraction (HW TLAS or SW global-SDF) → sample the lit surface cache → write the
    // mean indirect irradiance E into the HDR scene color. The keystone proof + the FAZ 6 screen-probe preview. Mirrors
    // the SDF/card debug pipelines (CBV b0 + root SRVs t0-t3 + 2-SRV G-buffer table t4/t5 + HeapDirectlyIndexed bindless
    // for the clipmap/FinalLighting reads + clamp sampler s0). Lazily built on the first debug frame.
    ID3D12RootSignature traceDbgRootSig;
    ID3D12PipelineState traceDbgPso;
    Dx12FrameCb<TraceDebugConstants> traceDbgCb;
    bool loggedTraceDbg;

    [StructLayout(LayoutKind.Sequential)]
    struct TraceDebugConstants {
        // --- the LumenTrace parameter block (MUST be first; the include reads these by name) ---
        public Vector3 LtClipOrigin;   public float LtVoxelSize;
        public Vector3 LtCamPosUnused; public float LtClipHalfExtent;
        public uint LtClipResX, LtClipResY, LtClipResZ; public float LtMaxTraceDist;
        public uint LtAtlasSize, LtCardCount, LtInstanceCount, LtFinalReadIdx;
        public uint LtClipmapIdx, LtFinalValid, LtHasTlas, LtSkyIdx;
        public float LtSkyIntensity, LtUseSky, LtSurfBias, LtPad0;
        // FAZ 11 — spatial card grid (matches LUMEN_TRACE_PARAMS tail in LumenTrace.hlsl)
        public Vector3 LtCgOrigin; public float LtCgEnabled;
        public Vector3 LtCgCellSize; public uint LtCgDim;
        public uint LtCgCellIdx, LtCgIndexIdx, LtCgPad0, LtCgPad1;
        // --- debug-view-only fields ---
        public Matrix4x4 InvViewProj;
        public Vector3 CamPos;     public uint RayCount;
        public uint PreferSW;      public uint FrameIndex;
        public uint DebugMode;     public float Intensity;
        public Vector2 DbgPad;
    }

    static int traceDebugDoor = -2;
    static bool TraceDebug() {
        if (traceDebugDoor == -2)
            traceDebugDoor = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_TRACE_DEBUG") == "1" ? 1 : 0;
        return traceDebugDoor == 1;
    }

    unsafe void EnsureTraceDebugPipeline()
    {
        if (traceDbgPso != null) return;
        // CBV b0 | root SRVs t0 TLAS / t1 cards / t2 pages / t3 ranges | 2-SRV G-buffer table (t4 depth, t5 normal) |
        // clamp sampler s0. HeapDirectlyIndexed so the include's clipmap + FinalLighting (+ optional sky) resolve from
        // ResourceDescriptorHeap[] (the SAME bound bindless heap serves the table AND the bindless reads).
        var cbv    = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var tlas   = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var cards  = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.All);
        var pages  = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(2, 0), ShaderVisibility.All);
        var ranges = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(3, 0), ShaderVisibility.All);
        var gbRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 2, baseShaderRegister: 4);   // t4,t5
        var gbTable = new RootParameter1(new RootDescriptorTable1(gbRange), ShaderVisibility.Pixel);
        var clamp = new StaticSamplerDescription(ShaderVisibility.All, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1, ComparisonFunction = ComparisonFunction.Never,
            MinLOD = 0, MaxLOD = float.MaxValue,
        };
        traceDbgRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(
                RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
                new[] { cbv, tlas, cards, pages, ranges, gbTable }, new[] { clamp })));

        // The debug shader #includes "Lumen/LumenTrace.hlsl"; there is NO DXC include handler (shaders are embedded
        // strings), so prepend the include source + strip the #include line — the established pattern (see Dx12NrdDenoiser).
        string inc  = EmbeddedShaderSource.ReadHlsl("Lumen/LumenTrace.hlsl");
        string body = EmbeddedShaderSource.ReadHlsl("Lumen/LumenTraceDebug.hlsl");
        body = System.Text.RegularExpressions.Regex.Replace(
            body, "(?m)^\\s*#include\\s+\"Lumen/LumenTrace\\.hlsl\".*$", inc);

        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, body, "VSDebug", "LumenTraceDebug.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, body, "PSDebug", "LumenTraceDebug.hlsl");
        traceDbgPso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = traceDbgRootSig, VertexShader = vs, PixelShader = ps, InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat },
            DepthStencilFormat = Format.Unknown, SampleDescription = new SampleDescription(1, 0),
        });
        traceDbgCb = new Dx12FrameCb<TraceDebugConstants>(dev);
    }

    unsafe void RecordTraceDebug(Dx12FrameContext ctx)
    {
        Dx12LumenCardScene cards = scene.CardScene;
        Dx12SceneAS sceneAS = ctx.Dxr?.SceneAS;
        bool hasTlas = sceneAS != null && sceneAS.Valid;
        // Backend: SW if forced or no TLAS; else HW. SW needs a built clipmap; if neither backend is usable, skip.
        bool forceSW = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_TRACE_SW") == "1";
        bool preferSW = forceSW || !hasTlas;
        bool sdfReady = globalSdf != null && globalSdf.Valid && globalSdf.ClipmapSrvBindless >= 0;

        if (cards is null || !cards.Valid || cards.CardCount == 0 || ctx.GBuffer == null ||
            (preferSW && !sdfReady) || (!preferSW && !hasTlas)) {
            if (!loggedTraceDbg) { loggedTraceDbg = true;
                Console.WriteLine($"[LumenTraceDebug] SKIP cards={(cards==null?"null":cards.CardCount.ToString())} " +
                    $"valid={cards?.Valid} hasTlas={hasTlas} preferSW={preferSW} sdfReady={sdfReady} finalValid={cards?.FinalValid}"); }
            return;
        }
        EnsureTraceDebugPipeline();
        globalSdf?.ToPixelShaderResource();   // SW march reads the clipmap as a (bindless) SRV — make it readable.

        Matrix4x4.Invert(ctx.ViewProj, out Matrix4x4 invVP);
        uint rays = (uint)Math.Clamp((int)EnvF("BALLISTIC_DX12_LUMEN_TRACE_RAYS", 8f), 1, 64);
        uint mode = (uint)Math.Clamp((int)EnvF("BALLISTIC_DX12_LUMEN_TRACE_MODE", 0f), 0, 2);
        float intensity = EnvF("BALLISTIC_DX12_LUMEN_TRACE_INTENSITY", 1f);
        float maxDist = EnvF("BALLISTIC_DX12_LUMEN_TRACE_MAXDIST",
            globalSdf != null ? globalSdf.ClipWorldExtent * 1.8f : 1e4f);
        int clipIdx = globalSdf?.ClipmapSrvBindless ?? -1;

        traceDbgCb.Write(new TraceDebugConstants {
            LtClipOrigin = globalSdf?.ClipOrigin ?? Vector3.Zero,
            LtVoxelSize = globalSdf?.ClipVoxelSize ?? 1f,
            LtClipHalfExtent = globalSdf?.ClipHalf ?? 1f,
            LtClipResX = (uint)(globalSdf?.ClipRes ?? 1), LtClipResY = (uint)(globalSdf?.ClipRes ?? 1),
            LtClipResZ = (uint)(globalSdf?.ClipRes ?? 1), LtMaxTraceDist = maxDist,
            LtAtlasSize = (uint)cards.AtlasSize, LtCardCount = (uint)cards.CardCount,
            LtInstanceCount = (uint)cards.InstanceCount, LtFinalReadIdx = (uint)Math.Max(cards.FinalReadSrvIdx, 0),
            LtClipmapIdx = (uint)Math.Max(clipIdx, 0), LtFinalValid = cards.FinalValid ? 1u : 0u,
            LtHasTlas = hasTlas ? 1u : 0u, LtSkyIdx = 0u,
            LtSkyIntensity = 0f, LtUseSky = 0f, LtSurfBias = 0.03f, LtPad0 = 0f,
            // FAZ 11 — spatial card grid (world-pos lookup accel; off → linear scan, byte-id)
            LtCgEnabled = cards.CardGridValid ? 1f : 0f, LtCgOrigin = cards.CardGridOrigin,
            LtCgCellSize = cards.CardGridCellSize, LtCgDim = (uint)Math.Max(cards.CardGridDim, 1),
            LtCgCellIdx = (uint)Math.Max(cards.CardGridCellBindless, 0), LtCgIndexIdx = (uint)Math.Max(cards.CardGridIndexBindless, 0),
            InvViewProj = Matrix4x4.Transpose(invVP),
            CamPos = ctx.CamPos, RayCount = rays,
            PreferSW = preferSW ? 1u : 0u, FrameIndex = (uint)ctx.FrameCounter,
            DebugMode = mode, Intensity = intensity, DbgPad = Vector2.Zero,
        });

        if (!loggedTraceDbg) { loggedTraceDbg = true;
            Console.WriteLine($"[LumenTraceDebug] DRAW backend={(preferSW?"SW":"HW")} rays={rays} mode={mode} " +
                $"cards={cards.CardCount} inst={cards.InstanceCount} finalReadIdx={cards.FinalReadSrvIdx} " +
                $"clipIdx={clipIdx} finalValid={cards.FinalValid} maxDist={maxDist:0.#}"); }

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        ulong cbAddr = traceDbgCb.Gpu;
        ulong cardAddr = cards.CardBufferGpuAddress;
        ulong pageAddr = cards.PageBufferGpuAddress;
        ulong rangeAddr = cards.RangeBufferGpuAddress != 0 ? cards.RangeBufferGpuAddress : cardAddr;
        ulong tlasAddr = hasTlas ? sceneAS.TlasAddress : 0;
        Dx12DescriptorHeap bindless = Dx12Backend.BindlessHeap;

        // The trace reads the clipmap + FinalLighting via ResourceDescriptorHeap[] → the SINGLE bound CBV/SRV/UAV heap
        // MUST be the bindless heap (where those reserved-tail descriptors live). So the G-buffer t4/t5 table is COPIED
        // into a dynamic bindless-heap range here (NOT a persistent reserved slot — re-stamped every frame, used
        // immediately within this same recorded draw, AFTER all GPU-driven work for the frame, so no Reset() clobbers it).
        int gbBase = bindless.AllocateRange(2);
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(gbBase + 0), ctx.GBuffer.DepthSrvCpu, heapType);     // t4 depth
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(gbBase + 1), ctx.GBuffer.ColorSrvCpu(1), heapType);  // t5 normal (RT1)
        GpuDescriptorHandle gbBindlessGpu = bindless.Gpu(gbBase);

        ctx.SceneColor.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(traceDbgRootSig);
            cl.SetPipelineState(traceDbgPso);
            cl.SetDescriptorHeaps(bindless.Heap);
            cl.SetGraphicsRootConstantBufferView(0, cbAddr);
            if (tlasAddr != 0) cl.SetGraphicsRootShaderResourceView(1, tlasAddr);
            cl.SetGraphicsRootShaderResourceView(2, cardAddr);
            cl.SetGraphicsRootShaderResourceView(3, pageAddr);
            cl.SetGraphicsRootShaderResourceView(4, rangeAddr);
            cl.SetGraphicsRootDescriptorTable(5, gbBindlessGpu);
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
    }

    static float EnvF(string name, float fallback) =>
        float.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.CultureInfo.InvariantCulture,
            out float v) ? v : fallback;

    // The product door. FAZ 9 — Lumen is now the DEFAULT GI (the full pipeline — surface cache, trace, screen-probe
    // GI, radiance cache, reflections — is verified on CornellBox AND Bistro-scale). The selection chain:
    //   1. BALLISTIC_DX12_LUMEN=1/0 — explicit per-system override (force Lumen on/off), highest priority.
    //   2. else the master GI selector BALLISTIC_DX12_GI = lumen | aurora | off (DEFAULT lumen when unset).
    // So with NO env set, Lumen runs. BALLISTIC_DX12_GI=aurora picks Aurora (Lumen yields — Aurora is KEPT behind the
    // door, NOT deleted, until Lumen is long-proven at production scale incl. async/perf). When Lumen is armed, its
    // card scene + SDF + screen-probe + reflections all cascade from this (CardsArmed()/SdfArmed() fold in Armed()).
    // A LumenVolume mirroring AuroraVolume (artist-facing, scene-driven) is a FAZ 10 follow-up; the master env selector
    // is the engine-wide default switch.
    static int envDoor = -2;   // -2 unread, -1 unset (follow master), 0 force-off, 1 force-on
    static int masterGi = -2;  // -2 unread, 0 aurora, 1 lumen (DEFAULT), 2 off
    public static bool Armed(Dx12FrameContext ctx)
    {
        if (envDoor == -2)
        {
            string v = Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN");
            envDoor = v == "1" ? 1 : v == "0" ? 0 : -1;
        }
        if (envDoor == 1) return true;
        if (envDoor == 0) return false;
        // Unset → follow the master GI selector (DEFAULT = lumen).
        return MasterGi() == 1;
    }

    // The engine-wide GI selector BALLISTIC_DX12_GI = lumen (DEFAULT) | aurora | off. Read once; shared so Aurora can
    // also honour it (BALLISTIC_DX12_GI=off must disable BOTH GI systems, not just hand the frame to Aurora). Returns
    // 1 lumen, 0 aurora, 2 off.
    public static int MasterGi()
    {
        if (masterGi == -2)
        {
            string g = (Environment.GetEnvironmentVariable("BALLISTIC_DX12_GI") ?? "lumen").Trim().ToLowerInvariant();
            masterGi = g == "aurora" ? 0 : g == "off" || g == "none" ? 2 : 1;   // default + any other value → lumen
        }
        return masterGi;
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

    // FAZ 10 — per-pass GPU timing. The Lumen sub-passes (capture/light/sdf/radiance-cache/screen-probe/reflections)
    // all record onto the SINGLE open frame list (ExecuteSync does not flush — see the determinism memory), so a
    // flush-per-pass timer (dev.GpuTimerBegin/End) would serialize the GPU and lie. Instead we use the deferred ring
    // profiler (Dx12GpuProfiler): it writes timestamp query pairs INTO the open list and drains them N frames later
    // after the fence completes — zero serialization, race-safe. Gated on BALLISTIC_DX12_GPU_PROFILE=1; the drained
    // "[GpuProf] LumenCapture=… LumenLight=… …" line lets us see WHICH Lumen sub-pass dominates the Bistro frame
    // before optimizing blind. Marks nest harmlessly inside the renderer's own marks (a flat timestamp-pair list).
    void GpuMark(string name) {
        var prof = dev.GpuProfiler;
        if (prof.Enabled && dev.FrameList is { } fl) prof.Begin(fl, name);
    }
    void GpuMarkEnd() {
        var prof = dev.GpuProfiler;
        if (prof.Enabled && dev.FrameList is { } fl) prof.End(fl);
    }

    // FAZ 10 — publish the (persisted) radiance cache's sampling params into ctx.LumenRc so consumers (transparent
    // forward at the Transparents event = BEFORE this GI pass; fog at event ~700 = after) can sample it. The cache
    // persists across frames, so calling this at frame setup hands the transparent pass last-frame's stable cache.
    // No-op until the cache exists + is valid (LumenRc stays Valid=false → consumers fall back to IBL/flat ambient).
    public void PublishRadianceCacheParams(Dx12FrameContext ctx)
    {
        if (radianceCache is null || !radianceCache.Valid) return;
        ctx.LumenRc = new LumenRcParamsForVolumetrics {
            Valid = true,
            Origin = radianceCache.Origin, ProbeSpacing = radianceCache.ProbeSpacingPub,
            GridRes = (uint)radianceCache.GridRes, AtlasInProbes = (uint)radianceCache.AtlasInProbesPub,
            ProbeRes = (uint)radianceCache.ProbeResPub, FinalProbeRes = (uint)radianceCache.FinalProbeResPub,
            TraceStop = radianceCache.TraceStop,
            IndirBindless = radianceCache.IndirBindless, RadBindless = radianceCache.RadBindless,
            HitBindless = radianceCache.HitBindless,
            IndirTex = radianceCache.IndirectionTex, RadTex = radianceCache.RadianceTex,
            HitTex = radianceCache.HitDistTex,
        };
    }

    public void Record(Dx12FrameContext ctx)
    {
        // FAZ 0: build/refresh the scene substrate ONLY. No GI is traced or combined → scene color is untouched,
        // GI stays black. The first armed frame logs the substrate counts (Dx12LumenScene.Ensure logs once per
        // stamp). Later phases trace SDF/screen probes here and additively combine indirect into the HDR color.
        // FAZ 3b: arm the card scene when Lumen GI is on (the card scene is part of the Lumen substrate). When only
        // the card door (BALLISTIC_DX12_LUMEN_CARDS=1) is set, Dx12LumenScene builds it from its own door instead.
        scene.SetLumenArmed(Armed(ctx));

        if (!scene.Ensure(ctx))
            return;   // no valid scene AS → nothing to build (Lumen is HW-RT only in FAZ 0; no software fallback)

        // FAZ 3c: CAPTURE the placed cards' material attributes (albedo / card-normal / emissive / card-depth) into
        // their atlas pages. Runs ONCE per (re)build (the card scene's own capturedStamp gate); a static scene captures
        // on the first armed frame and never again. No lighting — 3d lights the cache. Recorded into the open frame
        // list. Gated implicitly on the card scene existing (built whenever cards-or-Lumen is armed).
        GpuMark("LumenCapture");
        scene.CardScene?.Capture(ctx, ctx.Dxr.SceneAS);
        GpuMarkEnd();

        // FAZ 3d: LIGHT the surface cache. Per atlas texel: direct (sun + punctual + emissive NEE, shadow-rayed) +
        // indirect (radiosity gather of last frame's FinalLighting → multi-bounce) → a lit, view-independent
        // FinalLighting atlas. Runs every armed frame (lighting is dynamic; multi-bounce accumulates over frames).
        if (LightArmed(ctx))
        {
            GpuMark("LumenLight");
            scene.CardScene?.LightCards(ctx, ctx.Dxr.SceneAS);
            GpuMarkEnd();
        }

        // FAZ 2: build/refresh the camera-centered GLOBAL DISTANCE FIELD clipmap from the visible meshes' per-mesh
        // SDFs. Armed by BALLISTIC_DX12_GLOBALSDF=1 (independent test) OR whenever Lumen GI is on (the field is part
        // of the Lumen substrate). Builds NOTHING into the scene color by itself — FAZ 5 sphere-marches it for GI.
        if (SdfArmed(ctx))
        {
            globalSdf ??= new Dx12GlobalSdf(dev);
            GpuMark("LumenGlobalSdf");
            globalSdf.Build(ctx, ctx.Dxr.SceneAS, scene.DirtyThisFrame);
            GpuMarkEnd();

            // FAZ 2 verification artifact: sphere-trace the field per screen pixel into the HDR scene color so the
            // silhouette/surfaces appear where geometry is. Default off → scene color untouched. Guarded on a valid
            // (built) field so a scene with no SDFs (pre-v8 meshes) doesn't crash — just shows the empty-field bg.
            if (SdfDebug() && globalSdf.Valid && ctx.SceneColor != null)
                RecordSdfDebug(ctx);
        }

        // FAZ 3b verification artifact: ray-test the world-space card OBBs per screen pixel into the HDR scene color,
        // each hit shaded by its DirectionIndex color, so the card placement/orientation appears where geometry is.
        // Default off → scene color untouched. Drawn AFTER the SDF debug so the card view wins when both doors are on.
        if (CardDebug() && ctx.SceneColor != null)
            RecordCardDebug(ctx);

        // FAZ 3c verification artifact: blit the captured surface-cache atlas (albedo by default; selectable via
        // BALLISTIC_DX12_LUMEN_CAPTURE_VIEW) to the HDR scene color so the captured material attributes are visible.
        // Default off → scene color untouched. Drawn LAST so the capture view wins when multiple debug doors are on.
        if (CaptureDebug() && ctx.SceneColor != null)
            RecordCaptureDebug(ctx);

        // FAZ 3d verification artifact: blit the LIT surface-cache atlas (FinalLighting by default; DirectLighting via
        // BALLISTIC_DX12_LUMEN_LIGHT_VIEW=direct) to the HDR scene color so the lit cache is visible. Drawn LAST so the
        // lit view wins when multiple debug doors are on. Default off → scene color untouched.
        if (LightDebug() && ctx.SceneColor != null)
            RecordLightDebug(ctx);

        // FAZ 5 verification artifact (the KEYSTONE): per camera pixel, gather N cosine hemisphere rays through the
        // shared LumenTrace abstraction (HW TLAS or SW global-SDF) → sample the LIT surface cache at each hit → write
        // the mean indirect irradiance E into the HDR scene color. Drawn LAST (after LightCards lit the cache THIS
        // frame, so the trace samples this frame's lit cache — see the ordering note below) and after the SDF build so
        // the SW backend has a clipmap. Default off → scene color untouched. The preview of FAZ 6 (screen probes).
        if (TraceDebug() && ctx.SceneColor != null)
            RecordTraceDebug(ctx);

        // FAZ 6 — THE VISIBLE GI OUTPUT. When Lumen GI is armed (not a cards-only/SDF-only test), place sparse screen
        // probes, trace them via LumenTrace (sampling the LIT surface cache filled by LightCards above), integrate
        // per-pixel diffuse irradiance E, and ADD it to the scene color. This runs AFTER LightCards (so the probe
        // trace reads this frame's lit FinalLighting) and AFTER the SDF build (so the SW backend has a clipmap). The
        // deferred pass suppressed its IBL diffuse ambient (ctx.LumenActiveThisFrame), so this is the diffuse GI — no
        // double-count. Default off (BALLISTIC_DX12_LUMEN unset) → never reached → byte-identical render path.
        if (Armed(ctx) && ctx.SceneColor != null)
        {
            // FAZ 7: BUILD the world-space radiance cache FIRST (1-frame-deferred — allocate+trace+fixup the cells the
            // screen probes marked LAST frame), so the screen-probe gather below SAMPLES the now-filled cache on a
            // short-trace miss + marks cells for NEXT frame. Door-gated; when off the screen probe traces full distance
            // (FAZ 6 fallback) because Run() gets a null cache → RcEnabled=0 in the CB.
            if (RcArmed())
            {
                radianceCache ??= new Dx12LumenRadianceCache(dev);
                GpuMark("LumenRadianceCache");
                radianceCache.Build(ctx, scene.CardScene, globalSdf);
                GpuMarkEnd();
                if (Environment.GetEnvironmentVariable("BALLISTIC_DX12_LUMEN_RC_STATS") == "1")
                    radianceCache.DumpStats();

                // FAZ 10 — re-publish the radiance cache params (now THIS-frame's rebuilt cache) for the LATER fog pass
                // (volumetric GI, event ~700). The transparent pass already got LAST-frame's via PublishRadianceCacheParams
                // at frame setup (it runs before this GI pass). No-op when the cache isn't valid.
                PublishRadianceCacheParams(ctx);
            }

            screenProbe ??= new Dx12LumenScreenProbe(dev);
            GpuMark("LumenScreenProbe");
            screenProbe.Run(ctx, scene.CardScene, globalSdf, RcArmed() ? radianceCache : null);
            GpuMarkEnd();

            // FAZ 8 — LUMEN REFLECTIONS at event 600 timing (after the GI combine above). Each reflective G-buffer
            // pixel reflects through LumenTrace → the LIT surface cache, so a mirror surface carries the cache's GI
            // color. Runs AFTER the screen-probe combine wrote the diffuse GI into scene color (the reflection
            // composite reads/lerps the now-lit scene color). Gated on the REFL door; the existing Dx12ReflectionsPass
            // yields when this is active (ReflectionsActive → its WouldRun returns false). Default ON when Lumen on.
            if (ReflArmed())
            {
                reflections ??= new Dx12LumenReflections(dev);
                GpuMark("LumenReflections");
                reflections.Run(ctx, scene.CardScene, globalSdf);
                GpuMarkEnd();
            }
        }
    }

    public void Dispose()
    {
        scene?.Dispose();
        globalSdf?.Dispose();
        screenProbe?.Dispose();
        radianceCache?.Dispose();
        reflections?.Dispose();
        dbgPso?.Dispose(); dbgRootSig?.Dispose(); dbgCb?.Dispose(); dbgSrv?.Dispose();
        cardDbgPso?.Dispose(); cardDbgRootSig?.Dispose(); cardDbgCb?.Dispose();
        capDbgPso?.Dispose(); capDbgRootSig?.Dispose(); capDbgCb?.Dispose(); capDbgSrv?.Dispose();
        litDbgPso?.Dispose(); litDbgRootSig?.Dispose(); litDbgCb?.Dispose(); litDbgSrv?.Dispose();
        traceDbgPso?.Dispose(); traceDbgRootSig?.Dispose(); traceDbgCb?.Dispose();
    }
}
