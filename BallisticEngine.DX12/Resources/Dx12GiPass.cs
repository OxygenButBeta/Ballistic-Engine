using System;
using System.Numerics;
using System.Runtime.InteropServices;
using BallisticEngine; // RuntimeSet, IStaticMeshRenderer, GiMode, PostProcessSettings, RenderStats
using Vortice.Direct3D; // PrimitiveTopology
using Vortice.Direct3D12;
using Vortice.Dxc; // DxcShaderStage
using Vortice.DXGI; // Format, SampleDescription

namespace BallisticEngine.DX12;

// Global illumination — the SINGLE mode-branching GI pass (plan decision F / trap 4: per-mode passes would
// break the EnsureRt*-fallback + WarnNoRtOnce single-fire, so GI is ONE pass that branches internally). It
// folds together what chunks 1–9 left inline at the GlobalIllumination event:
//   - SSGI (SSILVB horizon-bitmask one-bounce gather → temporal → OIDN → combine)
//   - RT-GI (DxrGi cosine-hemisphere trace → the SHARED SSGI resolve tail) with its DDGI world-cache update
//     + the screen-probe final gather (the Lumen screen-trace → world-cache hierarchy)
//   - the EnsureRtGi()-fails → DrawSsgi fallback + the !sceneAS.Valid → DrawSsgi fallback (verbatim)
//   - the shared SsgiResolveAndCombine tail (motion temporal + OIDN denoise + composite) called by BOTH the
//     SSGI and the RT-GI/DDGI/screen-probe sources, and FillSsgiConstants shared by both.
// THIS is why chunk 6 fixed the SSGI crash INLINE and deferred the MOVE to here (the shared tail couldn't be
// split across the orchestrator and a pass). Everything below is a VERBATIM move of the inline bodies, only
// re-rooted onto ctx + this pass's own fields.
//
// Event = GlobalIllumination (500). Enabled = ctx.GiMode != Off (the giMode resolve + the no-RT auto-downgrade
// + WarnNoRtOnce stay in the orchestrator, which sets ctx.GiMode — wrap ORCHESTRATION only, never the Lumen
// algorithm: DDGI / screen-probe / DxrGi internals are frozen). The shared DXR substrate (sceneAS / device5 /
// the dxr-availability check / rtGeometry) lives in ctx.Dxr (Dx12DxrShared) — shared with RT shadows (inline)
// and the Reflections pass. Lumen-adjacent + most care: the last conversion of phase-1 step F.
public sealed class Dx12GiPass : IRenderPass, IDisposable
{
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.GlobalIllumination;
    public string Name => "GI";

    // The inline dispatch ran only when giMode != Off (RayTraced → EnsureRtGi?DrawRtGi:DrawSsgi; ScreenSpace →
    // DrawSsgi). doors.Minimal already forces giMode = Off in the orchestrator's resolve, so this gate also
    // reproduces the MINIMAL "GI off" behaviour. The RT-vs-SSGI branch + the EnsureRtGi fallback live in Record.
    public bool Enabled(Dx12FrameContext ctx) => ctx.GiMode != GiMode.Off;

    // PHASE-2 V1: reads the G-buffer (depth + normals for the SSGI/RT-GI gather) and read-modify-writes the HDR
    // scene color (it gathers indirect from `target`, then CopyColorFrom(ssgiScene) back into `target` — the
    // GI-enriched scene becomes the new SceneColor). The RT-GI branch additionally uses the DXR scene AS, but
    // that's modeled as an immobile inline-core resource in V1 (the orchestrator owns ctx.Dxr); declaring the
    // raster G-buffer + SceneColor is sufficient for the V1 order/cull (RT-GI is excluded from the golden gate).
    public void Declare(Dx12PassBuilder b)
    {
        b.Read(b.Resource("GBuffer"));
        b.ReadWrite(b.Resource("SceneColor"));
        // PHASE-2 V3 (chunk 16): derive ONLY the SCREEN-PATH (DrawSsgi) shared boundary head — the SSGI gather
        // reads the lit HDR scene color + G-buffer depth as SRVs (target.ColorToShaderResource() +
        // gbuffer.DepthToShaderResource(), DrawSsgi head). Derive both. EVERYTHING ELSE stays inline:
        //   - the RT branch's gbuffer.DepthToNonPixelShaderResource() (RT raygen, OUT of V3 scope),
        //   - the RT branch's own target.ColorToShaderResource() (DrawRtGi — idempotent after the derived emit),
        //   - the pass-private scratch transitions in SsgiResolveAndCombine (ssgiTarget/ssgiDenoised/ssgiScene/
        //     history) — they are mid-pass, not pass-boundary heads.
        // The derived emit fires before Record for EVERY GI mode (incl. RT): a redundant SceneColor->PSR +
        // depth->PSR before the RT branch re-transitions depth->NonPixel — idempotent, harmless. RT GI is excluded
        // from the golden gate; the deterministic matrix exercises only the ScreenSpace (DrawSsgi) path.
        b.DeriveBarriers();
        b.Use(Dx12ResourceUsage.SceneColorShaderRead);
        b.Use(Dx12ResourceUsage.GBufferDepthShaderRead);
    }

    readonly Dx12Device dev;

    // === SSGI: SSILVB horizon-bitmask one-bounce gather (half-res) → composite into the lit HDR scene. ===
    ID3D12RootSignature ssgiRootSig; // SsgiConstants CBV(b0) + 3-SRV table + clamp sampler
    ID3D12PipelineState ssgiGatherPso, ssgiTemporalPso, ssgiCombinePso;
    ID3D12Resource ssgiCb;
    unsafe byte* ssgiCbMapped;
    Dx12OffscreenTarget ssgiTarget; // half-res RGBA16F raw GI (rgb + edge-fade)
    Dx12OffscreenTarget ssgiHistoryA, ssgiHistoryB; // half-res ping-pong accumulated GI (rgb + history len)
    Dx12OffscreenTarget ssgiDenoised; // half-res OIDN output (the GL a-trous replacement)
    Dx12OffscreenTarget ssgiScene; // full-res scratch: combine writes here, copied back to `target`
    Dx12DescriptorHeap ssgiSrvVisible; // 3 SRVs per pass
    int ssgiFrame; // slice-set rotation counter
    bool ssgiHistWriteB; // temporal ping-pong toggle

    bool ssgiHistValid; // false on first frame / resize

    // OIDN spatial denoise (replaces the GL a-trous). Two paths: a ZERO-COPY GPU path (OIDN's HIP device
    // imports a D3D12 shared buffer) when SharedCapable, else the CPU readback round-trip. Lazy on first use.
    Dx12OidnDenoiser ssgiOidn;
    bool ssgiOidnTried;
    float[] ssgiCpuColor, ssgiCpuOut; // host float3 buffers sized to the half-res GI (readback path)
    Dx12OidnGpuPath ssgiOidnGpu; // zero-copy GPU pack/unpack denoise (shared float buffer)
    bool ssgiSharedFailed; // shared path failed once → stick to readback forever
    bool ssgiOidnForceReadback; // BALLISTIC_DX12_OIDN_READBACK=1 → force the CPU path (A/B door)
    bool ssgiOidnTiming; // BALLISTIC_DX12_OIDN_TIMING=1 → log avg denoise ms (perf A/B)
    bool ssgiOidnGuide; // P6.1 BALLISTIC_DX12_OIDN_GUIDE=1 → albedo+normal AOV guides
    bool ssgiAuxFailed; // aux import failed once → denoise unguided forever (graceful)
    bool ssgiOidnEnvRead;
    readonly System.Diagnostics.Stopwatch ssgiOidnSw = new();
    double ssgiOidnAccumMs;
    int ssgiOidnAccumFrames;

    [StructLayout(LayoutKind.Sequential)]
    struct SsgiConstants
    {
        public Matrix4x4 Projection;
        public Matrix4x4 InvProjection;
        public Matrix4x4 ViewMatrix;
        public Vector4 Params0; // RayLength, Falloff, Thickness, MultiBounce
        public Vector4 Params1; // BounceBoost, RayCount, FrameIndex, _
        public Vector4 Params2; // TexelSize.xy, preExposure, 1/preExposure
        public Vector4 Combine0; // Intensity, Look, Saturation, OcclusionPower
        public Vector4 Tint; // Tint.xyz, _
        public Vector4 Params3; // HasHistory, MaxHistory, _, _
    }

    // === DXR ray-traced GI (GI volume Off/SSGI/RT-GI enum: PostFX.GiMode) ===
    ID3D12RootSignature rtGiRootSig; // CBV b0/b1 + table{t0-t4,u0} + SRV t5 materials + t6 instances + bindless
    ID3D12StateObject rtGiPso;
    ID3D12Resource rtGiSbt, rtGiCb, rtGiSunCb;
    unsafe byte* rtGiCbMapped, rtGiSunCbMapped;
    Dx12DescriptorHeap rtGiHeap; // 6 descriptors (rebuilt per frame)
    bool rtGiBuilt;
    const int RtSbtSlot = 64; // shader-table record alignment

    [StructLayout(LayoutKind.Sequential)]
    struct RtGiConstants
    {
        public Matrix4x4 InvViewProj;
        public Matrix4x4 ViewProj;
        public Vector4 Params;
    } // preExp, rayLength, emissiveEnable, frameIdx

    [StructLayout(LayoutKind.Sequential)]
    struct RtGiSun
    {
        public Vector3 SunDir;
        public float NormalBias;
        public Vector3 SunColor;
        public float LightCount;
    }

    // P2: DDGI world-probe radiance cache (BALLISTIC_DX12_DDGI=1). The instance lives in the SHARED holder
    // (ctx.Dxr.Ddgi) because the Reflections pass (event 600, after GI 500) reads its atlas/grid/ProbeState as
    // the RT-reflection hit ambient — exactly as the inline DrawRtReflections read the renderer's `ddgi` field.
    // This pass is its sole CREATOR + updater. P4: screen-space radiance probes (final gather, miss → DDGI),
    // lazily created + private. The shared bindless-heap tail reservations (RtGi/DDGI/ScreenProbe) match the
    // inline constants exactly (the Reflections pass keeps its own RtRefl tail below).
    bool ddgiLogged;
    bool ddgiDebugDumped;
    int probeColorThrottle;   // throttles the editor probe-colour readback (every ~12th GI frame when requested)
    Dx12ScreenProbe screenProbe; // P4: screen-space radiance probes (final gather)
    bool screenProbeLogged;

    // The RT-GI / DDGI / screen-probe table descriptors live in the SAME bindless heap as the material/geometry
    // SRVs (one CBV/SRV/UAV heap binds at a time), in the heap's RESERVED TAIL. R1.1: the bases are no longer
    // hand-written `16384 - N` magic numbers — they come from the single Dx12BindlessTail allocator, which
    // compile-time-asserts the layout (byte-identical to the old constants; see Dx12BindlessTail.cs).
    //   RtGi: 6 used (TLAS @ +0, depth +1, normal +2, irr cube +3, lit scene +4, ssgiTarget UAV +5).
    //   DDGI: 3 used (TLAS @ +0, irr cube +1, prev-irr atlas +2).
    //   ScreenProbe: 3 used (TLAS @ +0, irr cube +1, DDGI atlas +2).
    const int RtGiTableBase = Dx12BindlessTail.RtGiTableBase;
    const int DdgiTableBase = Dx12BindlessTail.DdgiTableBase;
    const int DdgiFarTableBase = Dx12BindlessTail.DdgiFarTableBase;   // CHUNK3 far cascade trace block
    const int ScreenProbeTableBase = Dx12BindlessTail.ScreenProbeTableBase;

    // CHUNK3: the FAR (sparse, wide) cascade — its own Dx12Ddgi, traced + blended like near each frame, sampled by
    // the gather as the fallback where near has no coverage. Created only when cascade=2 (BALLISTIC_DX12_DDGI_CASCADES
    // >= 2). Reflections keep reading the NEAR cascade (ctx.Dxr.Ddgi); far augments diffuse coverage only.
    Dx12Ddgi ddgiFar;
    int? cascadeCountEnv;
    int CascadeCount {
        get {
            if (cascadeCountEnv == null)
                cascadeCountEnv = int.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_CASCADES"),
                    out int n) && n >= 1 ? Math.Min(n, 2) : 1;
            return cascadeCountEnv.Value;
        }
    }

    // BuildSsgi + the SSGI/RT-GI rootsig/PSO/CB/heap construction moved VERBATIM into the ctor (re-rooted onto
    // dev). The SSGI PSOs/targets are built here; the RT-GI pipeline (rtGi*) stays LAZY (EnsureRtGi on first
    // RayTraced use) exactly as inline — DXR may be unavailable. DDGI/screenProbe are also lazy. The ctor
    // allocates the SSGI targets at the initial resolution (the inline BuildSsgi called AllocSsgiTargets() at
    // its end; the SSAO/TAA/Composite passes follow the same ctor(dev,w,h) → Resize pattern) so the first frame
    // — which can run before any graph.Resize — already has valid targets. graph.Resize re-fans them on resize.
    public unsafe Dx12GiPass(Dx12Device device, int width, int height)
    {
        dev = device;
        BuildSsgi();
        Resize(width, height);
    }

    // ===== ENABLED-PASS RECORD: the inline GI dispatch (DX12HDRenderer ~1631) moved VERBATIM. =====
    // RayTraced → EnsureRtGi() ? DrawRtGi : DrawSsgi; ScreenSpace → DrawSsgi. The per-effect TimePass tag the
    // inline code used (GI:RT / GI:SSGI) is dropped — the GRAPH already times the pass under Name ("GI"); the
    // inner DDGI/Gather/ScreenProbe sub-stopwatches inside DrawRtGi keep adding their own GpuPasses entries.
    public unsafe void Record(Dx12FrameContext ctx)
    {
        Dx12RenderTargetPool.PoolBarrier(ctx.Dev, "ssgiTarget", "ssgiDenoised",
            "ssgiScene"); // V2: aliasing barrier + discard the produced placed targets (no-op when pool off)
        if (ctx.GiMode == GiMode.RayTraced)
        {
            if (EnsureRtGi(ctx)) DrawRtGi(ctx);
            else DrawSsgi(ctx);
        }
        else if (ctx.GiMode == GiMode.ScreenSpace)
        {
            DrawSsgi(ctx);
        }
    }

    // ssgiTarget exposed for the still-inline NORT raster-probe DEBUG blit (DrawRasterProbeMeasure, off by
    // default — BALLISTIC_DX12_NORT_PROBES_DEBUG=1). The probe diagnostic stays inline (it reaches deep into
    // the orchestrator's geometry helpers); it blits the albedo cube into this pass's ssgiTarget so a GI-isolate
    // capture shows the probe. Not part of the GI mode-branch; a measurement-only seam.
    public Dx12OffscreenTarget SsgiTarget => ssgiTarget;

    // The film-grain animation counter (was DX12HDRenderer.ssgiFrame). FillSsgiConstants increments it during
    // GI Record (after ctx is built); the composite reads it via ctx.GrainFrame, which the orchestrator
    // refreshes from this getter just before the composite window — the non-deterministic grain phase only.
    public int SsgiFrame => ssgiFrame;

    // ============================== SSGI ==============================

    // Screen-space GI (SSILVB horizon-bitmask gather): half-res gather reads the lit HDR color + G-buffer
    // depth/normal → ssgiTarget (raw one-bounce); the shared resolve adds it into the scene.
    unsafe void DrawSsgi(Dx12FrameContext ctx)
    {
        var dev = ctx.Dev;
        var target = ctx.SceneColor;
        var gbuffer = ctx.GBuffer;
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        FillSsgiConstants(ctx);

        // PHASE-2 V3: skip the manual SceneColor + depth heads when derived barriers are active (the graph emitted
        // ctx.SceneColor.ColorToShaderResource() + gbuffer.DepthToShaderResource() before Record). DrawSsgi is also
        // the EnsureRtGi-fails / !sceneAS.Valid fallback called from DrawRtGi — by then the derived (or RT-branch)
        // transitions already ran, so these are idempotent no-ops either way.
        if (!ctx.BarriersDerived)
        {
            target.ColorToShaderResource();
            gbuffer.DepthToShaderResource();
        }
        // motion RT (gbuffer RT4) is already PixelShaderResource (ToShaderResource transitioned all colors).

        // Gather (half-res) → ssgiTarget. SRVs: color t0, depth t1, normal t2.
        ssgiSrvVisible.Reset();
        int gb = ssgiSrvVisible.AllocateRange(3);
        dev.Device.CopyDescriptorsSimple(1, ssgiSrvVisible.Cpu(gb + 0), target.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssgiSrvVisible.Cpu(gb + 1), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssgiSrvVisible.Cpu(gb + 2), gbuffer.ColorSrvCpu(1), heapType);
        ssgiTarget.RenderColorOnly(cl =>
        {
            cl.SetGraphicsRootSignature(ssgiRootSig);
            cl.SetPipelineState(ssgiGatherPso);
            cl.SetDescriptorHeaps(ssgiSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, ssgiCb.GPUVirtualAddress);
            cl.SetGraphicsRootDescriptorTable(1, ssgiSrvVisible.Gpu(gb));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });

        SsgiResolveAndCombine(ctx, resetRing: false); // gather already Reset()+used slots 0-2; resolve continues 3-8
    }

    // Fill the shared SsgiConstants CBV (dials + matrices + pre-exposure + history flag). Used by the SSGI
    // gather AND the RT-GI gather (temporal/combine read it). Returns this frame's rotation index.
    unsafe int FillSsgiConstants(Dx12FrameContext ctx)
    {
        var proj = ctx.Proj;
        var view = ctx.View;
        Matrix4x4.Invert(proj, out Matrix4x4 invProj);
        var pf = ctx.PostFX;
        int fi = ssgiFrame++ & 1023;
        // chunk 11 (step-G collapse): publish the POST-increment grain counter into ctx so the composite reads
        // the freshly-produced value within the SINGLE graph.Execute (GI event 500 runs before Composite 700).
        // Was the orchestrator's `ctx.GrainFrame = giPass.SsgiFrame` line between the old GI/Composite windows;
        // when GI is Off the pass doesn't run, so the orchestrator still seeds ctx.GrainFrame = giPass.SsgiFrame
        // (un-incremented) before Execute. Deterministic capture freezes grain to 0 regardless, so this is
        // live-path-only — but kept exact to avoid a silent off-by-one in the grain animation phase.
        ctx.GrainFrame = ssgiFrame;
        float preExp = float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_EXPOSURE"),
            System.Globalization.CultureInfo.InvariantCulture, out float e)
            ? e
            : 1.0e-5f;
        float invPreExp = preExp > 0f ? 1f / preExp : 0f;
        *(SsgiConstants*)ssgiCbMapped = new SsgiConstants
        {
            Projection = Matrix4x4.Transpose(proj), InvProjection = Matrix4x4.Transpose(invProj),
            ViewMatrix = Matrix4x4.Transpose(view),
            Params0 = new Vector4(pf.SsgiRayLength, pf.SsgiFalloff, pf.SsgiThickness, 0f),
            Params1 = new Vector4(MathF.Max(pf.SsgiBounceBoost, 0f), Math.Clamp(pf.SsgiRayCount, 1, 8), fi,
                Math.Clamp(pf.SsgiTemporalClamp, 1f, 4f)),   // w = temporal neighbourhood-clamp inflation (live dial)
            Params2 = new Vector4(1f / ssgiTarget.Width, 1f / ssgiTarget.Height, preExp, invPreExp),
            Combine0 = new Vector4(pf.SsgiIntensity, Math.Clamp(pf.SsgiLook, 0f, 1f),
                MathF.Max(pf.SsgiSaturation, 0f), MathF.Max(pf.SsgiOcclusionPower, 0f)),
            Tint = new Vector4(pf.SsgiTint.X, pf.SsgiTint.Y, pf.SsgiTint.Z, 0f),
            // HasHistory=0 in deterministic capture → PSTemporal returns the current GI directly (the SSGI/DDGI
            // temporal EMA is frame-count-dependent → would defeat byte-diffable captures).
            Params3 = new Vector4((ssgiHistValid && !ctx.DeterministicCapture) ? 1f : 0f,
                MathF.Max(pf.SsgiMaxHistory, 1f), GiIsolateOn(ctx) ? 1f : 0f,
                MathF.Max(pf.SsgiGhostingReject, 0f)),   // w = ghosting-reject motion slope (live dial)
        };
        return fi;
    }

    // Shared GI resolve tail: motion-buffer temporal accumulation + OIDN denoise + composite into the scene.
    // ssgiTarget holds the raw (noisy) one-bounce GI (from either the SSILVB gather or the RT gather); the
    // SsgiConstants CBV must already be filled. Used by both DrawSsgi and DrawRtGi.
    // P0a: `resetRing` — DrawSsgi passes FALSE (its gather already Reset()+used slots 0-2; the resolve continues
    // 3-8 — the heap is sized 9 = one frame). DrawRtGi passes TRUE (it didn't touch the ring before the resolve).
    unsafe void SsgiResolveAndCombine(Dx12FrameContext ctx, bool resetRing = true)
    {
        var dev = ctx.Dev;
        var target = ctx.SceneColor;
        var gbuffer = ctx.GBuffer;
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Dx12OffscreenTarget histRead = ssgiHistWriteB ? ssgiHistoryA : ssgiHistoryB;
        Dx12OffscreenTarget histWrite = ssgiHistWriteB ? ssgiHistoryB : ssgiHistoryA;

        // Temporal (half-res) → histWrite. SRVs: currentGI t0, historyGI t1, motion t2 (gbuffer RT4).
        if (resetRing) ssgiSrvVisible.Reset();
        ssgiTarget.ColorToShaderResource();
        histRead.ColorToShaderResource();
        int tb = ssgiSrvVisible.AllocateRange(3);
        dev.Device.CopyDescriptorsSimple(1, ssgiSrvVisible.Cpu(tb + 0), ssgiTarget.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssgiSrvVisible.Cpu(tb + 1), histRead.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssgiSrvVisible.Cpu(tb + 2), gbuffer.ColorSrvCpu(Dx12GBuffer.MotionRtIndex),
            heapType);
        histWrite.RenderColorOnly(cl =>
        {
            cl.SetGraphicsRootSignature(ssgiRootSig);
            cl.SetPipelineState(ssgiTemporalPso);
            cl.SetDescriptorHeaps(ssgiSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, ssgiCb.GPUVirtualAddress);
            cl.SetGraphicsRootDescriptorTable(1, ssgiSrvVisible.Gpu(tb));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });

        // OIDN spatial denoise (replaces the GL a-trous). Preferred: the ZERO-COPY GPU path. Fallback: the CPU
        // readback round-trip. BALLISTIC_DX12_SSGI_OIDN=0 skips denoise (A/B); degrades gracefully without DLLs.
        Dx12OffscreenTarget giForCombine = histWrite;
        bool oidnOn = Environment.GetEnvironmentVariable("BALLISTIC_DX12_SSGI_OIDN") != "0";
        if (oidnOn)
        {
            if (!ssgiOidnEnvRead)
            {
                ssgiOidnEnvRead = true;
                ssgiOidnForceReadback = Environment.GetEnvironmentVariable("BALLISTIC_DX12_OIDN_READBACK") == "1";
                ssgiOidnTiming = Environment.GetEnvironmentVariable("BALLISTIC_DX12_OIDN_TIMING") == "1";
                ssgiOidnGuide = Environment.GetEnvironmentVariable("BALLISTIC_DX12_OIDN_GUIDE") == "1";
            }

            if (!ssgiOidnTried)
            {
                ssgiOidnTried = true;
                ssgiOidn = new Dx12OidnDenoiser(dev.AdapterLuidBytes);
            }

            if (ssgiOidn != null && ssgiOidn.Valid)
            {
                int w = ssgiTarget.Width, h = ssgiTarget.Height;
                bool usedZeroCopy = false;
                if (ssgiOidnTiming) ssgiOidnSw.Restart();
                // Zero-copy GPU denoise (no CPU round-trip): pack the GI texture into a shared FLOAT buffer on
                // the GPU, OIDN denoises it in place on the GPU, unpack back — float precision, ~12x faster.
                if (ssgiOidn.SharedCapable && !ssgiSharedFailed && !ssgiOidnForceReadback)
                {
                    if (ssgiOidnGpu == null) ssgiOidnGpu = new Dx12OidnGpuPath(dev);
                    if (ssgiOidnGpu.Ensure(ssgiOidn, ssgiDenoised.RenderTarget, w, h))
                    {
                        // P6.1: build + import the albedo/normal AOV guides ONCE (per size), then pack them from
                        // the G-buffer each frame so OIDN denoises EDGE-AWARE. If aux setup fails, fall through.
                        if (ssgiOidnGuide && !ssgiAuxFailed)
                        {
                            // The G-buffer color RTs sit in the combined Pixel|NonPixel shader-read state from
                            // the deferred pass, so the PackAux compute SRV reads of RT0 albedo / RT1 normal are
                            // already valid — no barrier.
                            if (ssgiOidnGpu.EnsureAux() && ssgiOidn.ImportAuxBuffers(ssgiOidnGpu.AlbedoHandle,
                                    ssgiOidnGpu.NormalHandle, ssgiOidnGpu.AuxByteSize))
                            {
                                ssgiOidnGpu.PackAux(gbuffer.ColorSrvCpu(0), gbuffer.ColorSrvCpu(1), target.Width,
                                    target.Height);
                            }
                            else
                            {
                                ssgiOidnGpu.ReleaseAux();
                                ssgiAuxFailed = true;
                            } // import failed → free unused aux buffers + go unguided
                        }

                        histWrite.ColorToNonPixelShaderResource(); // GI texture as a compute SRV
                        ssgiDenoised.ColorToUnorderedAccess(); // denoise target as a compute UAV
                        ssgiOidnGpu.Pack(histWrite.ColorSrvCpu); // GPU: texture -> shared float buf
                        if (ssgiOidn.ExecuteShared())
                        {
                            // GPU: OIDN denoise in place
                            ssgiOidnGpu.Unpack(); // GPU: shared float buf -> texture
                            ssgiDenoised.ColorToShaderResource(); // for the combine
                            giForCombine = ssgiDenoised;
                            usedZeroCopy = true;
                        }
                        else
                        {
                            ssgiSharedFailed = true;
                        } // HIP execute failed → readback from now on
                    }
                    else
                    {
                        ssgiSharedFailed = true;
                    } // import failed → readback from now on
                }

                // CPU readback fallback (shared path unavailable/failed/forced off this frame). The readback path
                // stays UNGUIDED (null albedo/normal) — guiding it would mean reading back + downsampling 2
                // full-res G-buffer textures on the CPU each frame, a large cost for the slow fallback.
                if (ReferenceEquals(giForCombine, histWrite))
                {
                    int n = w * h * 3;
                    if (ssgiCpuColor == null || ssgiCpuColor.Length != n)
                    {
                        ssgiCpuColor = new float[n];
                        ssgiCpuOut = new float[n];
                    }

                    histWrite.ReadColorRgb(ssgiCpuColor);
                    if (ssgiOidn.DenoiseHdr(ssgiCpuColor, null, null, ssgiCpuOut, w, h))
                    {
                        ssgiDenoised.WriteColorRgb(ssgiCpuOut); // leaves ssgiDenoised in PixelShaderResource
                        giForCombine = ssgiDenoised;
                    }
                }

                if (ssgiOidnTiming && !ReferenceEquals(giForCombine, histWrite))
                {
                    ssgiOidnSw.Stop();
                    ssgiOidnAccumMs += ssgiOidnSw.Elapsed.TotalMilliseconds;
                    ssgiOidnAccumFrames++;
                    if (ssgiOidnAccumFrames % 30 == 0)
                        Console.WriteLine(
                            $"[OIDN] denoise avg {ssgiOidnAccumMs / ssgiOidnAccumFrames:F2}ms/frame over {ssgiOidnAccumFrames} ({(usedZeroCopy ? "ZERO-COPY" : "READBACK")})");
                }
            }
        }

        if (ReferenceEquals(giForCombine, histWrite)) histWrite.ColorToShaderResource(); // temporal-only path

        // Combine (full-res) → ssgiScene, reading scene (t0) + (denoised) GI (t1; t2 unused, valid descriptor).
        target.ColorToShaderResource(); // scene must be readable (no-op if the gather already set it)
        int cbi = ssgiSrvVisible.AllocateRange(3);
        dev.Device.CopyDescriptorsSimple(1, ssgiSrvVisible.Cpu(cbi + 0), target.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssgiSrvVisible.Cpu(cbi + 1), giForCombine.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssgiSrvVisible.Cpu(cbi + 2), giForCombine.ColorSrvCpu, heapType);
        ssgiScene.RenderColorOnly(cl =>
        {
            cl.SetGraphicsRootSignature(ssgiRootSig);
            cl.SetPipelineState(ssgiCombinePso);
            cl.SetDescriptorHeaps(ssgiSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, ssgiCb.GPUVirtualAddress);
            cl.SetGraphicsRootDescriptorTable(1, ssgiSrvVisible.Gpu(cbi));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
        ssgiScene.ColorToShaderResource();
        target.CopyColorFrom(ssgiScene); // the GI-enriched scene becomes the new scene color

        ssgiHistWriteB = !ssgiHistWriteB; // ping-pong; this frame's accumulation is next frame's history
        ssgiHistValid = true;
    }

    // ============================== RT-GI ==============================

    // Lazily build the DXR GI pipeline. Reuses the shared device5 + sceneAS (ctx.Dxr). Returns false (→ SSGI
    // fallback) without DXR.
    unsafe bool EnsureRtGi(Dx12FrameContext ctx)
    {
        if (!ctx.Dxr.CheckAvailable("RTGI")) return false;
        if (rtGiBuilt) return true;
        rtGiBuilt = true;

        var dev = ctx.Dev;
        var device5 = ctx.Dxr.Device5;

        // P1 world-radiance hit shading: the closest-hit shader decodes the hit MATERIAL bindlessly, so the root
        // sig must allow ResourceDescriptorHeap[] indexing. Layout:
        //   CBV b0 RtGiConstants | CBV b1 RtGiSun | table{SRV t0-t4, UAV u0} |
        //   SRV t5 GpuMaterials (root) | SRV t6 RtInstance[] (root) + static clamp + wrap samplers.
        var cbv0 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0),
            ShaderVisibility.All);
        var cbv1 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0),
            ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 5, baseShaderRegister: 0);
        var uavRange = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 0);
        var table = new RootParameter1(new RootDescriptorTable1(srvRange, uavRange), ShaderVisibility.All);
        var matSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(5, 0),
            ShaderVisibility.All);
        var instSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(6, 0),
            ShaderVisibility.All);
        var lightSrv = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(7, 0),
            ShaderVisibility.All); // punctual lights
        var clampSamp = new StaticSamplerDescription(ShaderVisibility.All, 0, 0)
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        var wrapSamp = new StaticSamplerDescription(ShaderVisibility.All, 1, 0)
        {
            // albedo texture sampling
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        rtGiRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(
                RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
                new[] { cbv0, cbv1, table, matSrv, instSrv, lightSrv }, new[] { clampSamp, wrapSamp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("DxrGi.hlsl");
        byte[] dxil = Dx12ShaderCompiler.Compile(DxcShaderStage.Library, hlsl, "", "DxrGi.hlsl");
        var subs = new[]
        {
            new StateSubObject(new DxilLibraryDescription(dxil,
                new ExportDescription("RayGen"), new ExportDescription("Miss"), new ExportDescription("ClosestHit"))),
            new StateSubObject(new HitGroupDescription("HitGroup", HitGroupType.Triangles, "", "ClosestHit", "")),
            new StateSubObject(new RaytracingShaderConfig(16, 8)),
            new StateSubObject(new RaytracingPipelineConfig(1)),
            new StateSubObject(new GlobalRootSignature(rtGiRootSig)),
        };
        rtGiPso = device5.CreateStateObject(new StateObjectDescription(StateObjectType.RaytracingPipeline, subs));

        using ID3D12StateObjectProperties props = rtGiPso.QueryInterface<ID3D12StateObjectProperties>();
        uint idSize = Vortice.Direct3D12.D3D12.ShaderIdentifierSizeInBytes;
        rtGiSbt = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(RtSbtSlot * 3), ResourceStates.GenericRead);
        byte* sp = rtGiSbt.Map<byte>(0);
        System.Runtime.CompilerServices.Unsafe.CopyBlock(sp + 0 * RtSbtSlot, (void*)props.GetShaderIdentifier("RayGen"),
            idSize);
        System.Runtime.CompilerServices.Unsafe.CopyBlock(sp + 1 * RtSbtSlot, (void*)props.GetShaderIdentifier("Miss"),
            idSize);
        System.Runtime.CompilerServices.Unsafe.CopyBlock(sp + 2 * RtSbtSlot,
            (void*)props.GetShaderIdentifier("HitGroup"), idSize);
        rtGiSbt.Unmap(0);

        int cbSize = (Marshal.SizeOf<RtGiConstants>() + 255) & ~255;
        rtGiCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        rtGiCbMapped = rtGiCb.Map<byte>(0);
        int sunSize = (Marshal.SizeOf<RtGiSun>() + 255) & ~255;
        rtGiSunCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)sunSize), ResourceStates.GenericRead);
        rtGiSunCbMapped = rtGiSunCb.Map<byte>(0);
        rtGiHeap = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 6, shaderVisible: true);
        // The shared rtGeometry is built here on first use (??= in the holder); RT reflections reuses it.
        _ = ctx.Dxr.RtGeometry;
        return true;
    }

    // RT global illumination: trace a cosine-hemisphere ray per pixel → raw one-bounce GI in ssgiTarget, then
    // the SHARED SSGI resolve (temporal + OIDN + combine). The DDGI world cache + screen-probe gather hierarchy
    // live here. EnsureMaterialTable + rtGeometry.Ensure MUST run before the trace (bindless ids).
    unsafe void DrawRtGi(Dx12FrameContext ctx)
    {
        var dev = ctx.Dev;
        var target = ctx.SceneColor;
        var gbuffer = ctx.GBuffer;
        var ibl = ctx.Ibl;
        var gpuDriven = ctx.GpuDriven;
        var clusteredLights = ctx.ClusteredLights;
        var sceneAS = ctx.Dxr.SceneAS;
        var rtGeometry = ctx.Dxr.RtGeometry;
        Matrix4x4 view = ctx.View, viewProj = ctx.ViewProj, proj = ctx.Proj;
        Vector3 lightDir = ctx.LightDir, lightColor = ctx.LightColor, camPos = ctx.CamPos;

        sceneAS.Ensure(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection);
        if (!sceneAS.Valid)
        {
            DrawSsgi(ctx);
            return;
        } // no geometry → fall back to SSGI

        // --- DDGI world-probe radiance cache (BALLISTIC_DX12_DDGI=1). The instance is published to the shared
        // holder so the Reflections pass can read its atlas at event 600. ---
        if (DdgiEnabled(ctx))
        {
            if (ctx.Dxr.Ddgi == null) ctx.Dxr.Ddgi = new Dx12Ddgi(dev);
            var ddgi = ctx.Dxr.Ddgi;
            ddgi.Build();
            ddgi.Update(camPos);
            // CHUNK3: bring up the FAR cascade when cascade=2. Wider spacing (3x near) so it covers a far larger
            // volume at lower density; the near cascade keeps the detail. Only meaningful in BAKED mode (the
            // cascade is a coverage tool for the frozen field); the live path runs near only.
            if (CascadeCount >= 2 && ddgi.BakedMode)
            {
                if (ddgiFar == null) { ddgiFar = new Dx12Ddgi(dev); ddgiFar.CascadeSpacingMultiplier = 3f; }
                ddgiFar.SetBakedMode(true);
                ddgiFar.Build();
                ddgiFar.Update(camPos);
            }
            if (!ddgiLogged)
            {
                ddgiLogged = true;
                Vector3 o = ddgi.Origin;
                Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"[DDGI] grid {Dx12Ddgi.ProbesX}x{Dx12Ddgi.ProbesY}x{Dx12Ddgi.ProbesZ}={Dx12Ddgi.ProbeCount} probes; " +
                    $"origin=({o.X:0.#},{o.Y:0.#},{o.Z:0.#}) spacing={ddgi.Spacing.X:0.#}m covers ~{ddgi.Spacing.X * (Dx12Ddgi.ProbesX - 1):0}x{ddgi.Spacing.Y * (Dx12Ddgi.ProbesY - 1):0}x{ddgi.Spacing.Z * (Dx12Ddgi.ProbesZ - 1):0}m; " +
                    $"irrAtlas={Dx12Ddgi.IrradianceAtlasW}x{Dx12Ddgi.IrradianceAtlasH} depthAtlas={Dx12Ddgi.DepthAtlasW}x{Dx12Ddgi.DepthAtlasH}; {Dx12Ddgi.RaysPerProbe} rays/probe"));
                Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"[DDGI] round-robin 1/{ddgi.CurrentUpdateFraction}: {ddgi.ProbesPerFrame} probes/frame x {Dx12Ddgi.RaysPerProbe} = {ddgi.ProbesPerFrame * Dx12Ddgi.RaysPerProbe} rays/frame; " +
                    $"grid VRAM {ddgi.GridVramBytes / (1024.0 * 1024.0):0.0} MB"));
            }
        }

        // The bindless material table (byte-identical to the raster G-buffer) feeds the world-space hit shading;
        // ensure it (stamp-cached no-op if already built). Then the per-instance geometry SRVs.
        gpuDriven.EnsureMaterialTable(ctx.WholeMeshRenderers);
        rtGeometry.Ensure(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection, gpuDriven);

        int fi = FillSsgiConstants(ctx); // dials + matrices for the shared temporal/combine
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Matrix4x4.Invert(viewProj, out Matrix4x4 invVP);
        *(RtGiConstants*)rtGiCbMapped = new RtGiConstants
        {
            InvViewProj = Matrix4x4.Transpose(invVP), ViewProj = Matrix4x4.Transpose(viewProj),
            // Params.z = emissiveEnable — the GI hit adds emissive self-emission when >0.5.
            Params = new Vector4(SsgiPreExposure(), MathF.Max(ctx.PostFX.SsgiRayLength, 0.1f),
                GiEmissiveEnabled(ctx) ? 1f : 0f, fi),
        };
        Vector3 sunDir = lightDir.LengthSquared() < 1e-8f ? Vector3.UnitY : Vector3.Normalize(lightDir);
        *(RtGiSun*)rtGiSunCbMapped = new RtGiSun
        {
            SunDir = sunDir, NormalBias = 0.03f, SunColor = lightColor, LightCount = clusteredLights.LightCount,
        };

        // --- P2.1 DDGI probe update (trace+blend). Reuses the SAME bindless heap + root-SRV addresses + the
        // RtGiSun CBV as the RT-GI pass. Writes the trace table's 2 descriptors into the bindless tail, then
        // dispatches in its own ExecuteSyncImmediate (the multi-bounce feedback reads the PREVIOUS update's atlas
        // — see the comment; immediate flush keeps each iteration's completion exactly as the non-pipelined had). ---
        if (DdgiEnabled(ctx) && ctx.Dxr.Ddgi != null && ctx.Dxr.Ddgi.Allocated)
        {
            var ddgi = ctx.Dxr.Ddgi;
            ddgi.EmissiveEnabled = GiEmissiveEnabled(ctx);
            Dx12DescriptorHeap bh = Dx12Backend.BindlessHeap;
            var ddgiHeapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
            sceneAS.CreateTlasSrv(bh.Cpu(DdgiTableBase + 0)); // t0 TLAS
            dev.Device.CopyDescriptorsSimple(1, bh.Cpu(DdgiTableBase + 1), ibl.IrradianceSrv,
                ddgiHeapType); // t3 irr cube
            // t4 = LAST frame's irradiance atlas (P2.3 multi-bounce feedback).
            dev.Device.CreateShaderResourceView(ddgi.IrradianceTex, new ShaderResourceViewDescription
            {
                Format = Format.R16G16B16A16_Float,
                ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
            }, bh.Cpu(DdgiTableBase + 2));
            // t11 (table offset +3) = LAST frame's DEPTH-moments atlas — the Chebyshev LEAK GATE for the field
            // read (SampleIrradianceField). Without it a sealed-interior receiver pulled sky-lit probes sitting
            // OUTSIDE the wall → light leaking into a dark room. RG16F (mean, mean²).
            dev.Device.CreateShaderResourceView(ddgi.DepthTex, new ShaderResourceViewDescription
            {
                Format = Format.R16G16_Float,
                ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
            }, bh.Cpu(DdgiTableBase + 3));

            // One trace+blend+classify cycle (its own submit). `full` = warm-up / round-robin off (every probe).
            // P0a: ExecuteSyncImmediate (NOT the frame-list append) — the DDGI multi-bounce feedback reads the
            // PREVIOUS update's atlas, so each iteration must complete before the next. Submit/sync fix, NOT an
            // algorithm change.
            // STABILITY: a HIGHER hysteresis while the camera is static (0.97 → 0.985) lets the now-frozen-jitter
            // field settle to a steady value without boiling; a moving camera keeps the responsive 0.97 so new
            // light still appears quickly. The frozen jitter (Constants) is what actually stops the churn; this
            // just smooths the last bit of settle.
            float ddgiHyst = ddgi.IsStatic ? 0.985f : 0.97f;
            void RunDdgiUpdate(bool full) => dev.ExecuteSyncImmediate(cl =>
            {
                cl.SetDescriptorHeaps(bh.Heap);
                ddgi.DispatchDdgi(cl, bh, bh.Gpu(DdgiTableBase),
                    rtGiSunCb.GPUVirtualAddress, gpuDriven.MaterialsGpuAddress,
                    rtGeometry.InstancesGpuAddress, clusteredLights.LightBufGpuAddress,
                    hysteresis: ddgiHyst, intensity: MathF.Max(ctx.PostFX.SsgiIntensity, 0f),
                    feedback: true, // P2.3 multi-bounce
                    fullUpdate: full);
            });

            // --- P2.5 WARM-UP (capture-path determinism): on the FIRST DDGI frame, converge the field by
            // replaying the update many times FULL-grid, so a paused screenshot is the STEADY STATE. No-op in play. ---
            ddgi.TryWarmUp(() => RunDdgiUpdate(full: true));

            // FREEZE conditions: (1) the PAUSED capture path (warm-up converged it once); (2) CHUNK1 BAKED mode
            // once the progressive bake has converged the whole grid (IsBakeComplete). In both cases the per-frame
            // trace/blend is skipped → 0 rays/frame, only the gather samples the frozen field (the "performanssız +
            // ghosting" fix: no temporal feedback once frozen). While the bake is still rippling outward, we DO run
            // the per-frame update (full:false → the GPU band test picks the eligible wave), so the scene is
            // playable immediately and the far field fills in over frames.
            bool frozen = ddgi.WarmupEnabled || (ddgi.BakedMode && ddgi.IsBakeComplete);
            if (!frozen)
            {
                var ddgiSw = GiTimingEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
                RunDdgiUpdate(full: false); // live round-robin OR (baked) the GPU distance-band progressive wave
                if (ddgiSw != null)
                {
                    ddgiSw.Stop();
                    ctx.Stats.GpuPasses.Add(("GI:DDGI", ddgiSw.Elapsed.TotalMilliseconds));
                }
            }

            if (!ddgiDebugDumped && Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_DEBUG") == "1")
            {
                ddgiDebugDumped = true;
                ddgi.DumpIrradianceStats();
            }

            // PROBE-COLOUR readback for the editor gizmo (ShowProbeSpheres). Only while the gizmo requests it,
            // and THROTTLED to every ~12th GI frame (a texture readback is a full GPU sync — a few Hz is plenty
            // for a debug tint). The request flag is cleared so a closed gizmo stops the readback next frame.
            // The gizmo requests live probe colours; BALLISTIC_DX12_PROBE_COLORS=1 forces it headless (test door).
            if (GiDebugGrid.ProbeColorsRequested ||
                Environment.GetEnvironmentVariable("BALLISTIC_DX12_PROBE_COLORS") == "1")
            {
                if ((probeColorThrottle++ % 12) == 0) ddgi.ReadbackProbeColors();
                GiDebugGrid.ProbeColorsRequested = false;
            }

            // --- CHUNK3 FAR CASCADE trace+blend: same pattern as near, into its OWN bindless-tail block
            // (DdgiFarTableBase) so the two cascades don't clobber each other's trace descriptors. Wider grid,
            // progressively baked then frozen exactly like near. Only when cascade=2 + baked. ---
            if (ddgiFar != null && ddgiFar.Allocated)
            {
                sceneAS.CreateTlasSrv(bh.Cpu(DdgiFarTableBase + 0));   // t0 TLAS
                dev.Device.CopyDescriptorsSimple(1, bh.Cpu(DdgiFarTableBase + 1), ibl.IrradianceSrv, ddgiHeapType); // t3 irr cube
                dev.Device.CreateShaderResourceView(ddgiFar.IrradianceTex, new ShaderResourceViewDescription
                {
                    Format = Format.R16G16B16A16_Float,
                    ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                    Shader4ComponentMapping = ShaderComponentMapping.Default,
                    Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
                }, bh.Cpu(DdgiFarTableBase + 2));   // t4 prev irr atlas (far)
                dev.Device.CreateShaderResourceView(ddgiFar.DepthTex, new ShaderResourceViewDescription
                {
                    Format = Format.R16G16_Float,
                    ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                    Shader4ComponentMapping = ShaderComponentMapping.Default,
                    Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
                }, bh.Cpu(DdgiFarTableBase + 3));   // t11 prev depth atlas (far leak gate)

                ddgiFar.EmissiveEnabled = GiEmissiveEnabled(ctx);
                float farHyst = ddgiFar.IsStatic ? 0.985f : 0.97f;
                void RunFarUpdate(bool full) => dev.ExecuteSyncImmediate(cl =>
                {
                    cl.SetDescriptorHeaps(bh.Heap);
                    ddgiFar.DispatchDdgi(cl, bh, bh.Gpu(DdgiFarTableBase),
                        rtGiSunCb.GPUVirtualAddress, gpuDriven.MaterialsGpuAddress,
                        rtGeometry.InstancesGpuAddress, clusteredLights.LightBufGpuAddress,
                        hysteresis: farHyst, intensity: MathF.Max(ctx.PostFX.SsgiIntensity, 0f),
                        feedback: true, fullUpdate: full);
                });
                ddgiFar.TryWarmUp(() => RunFarUpdate(full: true));
                bool farFrozen = ddgiFar.WarmupEnabled || (ddgiFar.BakedMode && ddgiFar.IsBakeComplete);
                if (!farFrozen) RunFarUpdate(full: false);
            }

            // The compute gather/place reads depth as SRV t0 → NON_PIXEL state (shared by both GI sources below).
            gbuffer.DepthToNonPixelShaderResource();

            // --- PHASE 4: screen-space radiance probes (DEFAULT when DDGI is on; BALLISTIC_DX12_SCREENPROBE=0
            // opts out to the per-pixel DDGI gather below). The published Lumen screen-trace → world-cache
            // hierarchy. Same ssgiTarget contract → the shared resolve composites it. ---
            if (ScreenProbeEnabled(ctx))
            {
                DrawScreenProbeGather(ctx, invVP);
                SsgiResolveAndCombine(ctx);
                return;
            }

            // --- P2.2 DDGI GATHER: per-pixel sample the probe field → albedo*E pre-exposed into ssgiTarget,
            // REPLACING the RT per-pixel ray-march as the GI source. Then the shared SsgiResolveAndCombine. ---
            ssgiTarget.ColorToUnorderedAccess();
            var gatherSw = GiTimingEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
            dev.ExecuteSync(cl =>
            {
                ddgi.DispatchGather(cl, gbuffer.DepthSrvCpu, gbuffer.ColorSrvCpu(1), gbuffer.ColorSrvCpu(0),
                    ssgiTarget.RenderTarget, ssgiTarget.Width, ssgiTarget.Height,
                    Matrix4x4.Transpose(invVP), SsgiPreExposure(), ddgiFar);   // CHUNK3 far cascade fallback
            });
            if (gatherSw != null)
            {
                gatherSw.Stop();
                ctx.Stats.GpuPasses.Add(("GI:DDGIGather", gatherSw.Elapsed.TotalMilliseconds));
            }

            ssgiTarget.ColorToShaderResource();
            SsgiResolveAndCombine(ctx); // shared: motion temporal + OIDN + composite
            return;
        }

        // G-buffer is in the combined shader-read state; the lit scene color is the bounce source, so bring it
        // to SRV. The 6 table descriptors go into the BindlessHeap's reserved tail so the bindless hit shading
        // shares the heap.
        target.ColorToShaderResource();
        Dx12DescriptorHeap bindless = Dx12Backend.BindlessHeap;
        sceneAS.CreateTlasSrv(bindless.Cpu(RtGiTableBase + 0));
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(RtGiTableBase + 1), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(RtGiTableBase + 2), gbuffer.ColorSrvCpu(1),
            heapType); // world normal
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(RtGiTableBase + 3), ibl.IrradianceSrv,
            heapType); // off-screen fallback
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(RtGiTableBase + 4), target.ColorSrvCpu,
            heapType); // lit scene color
        dev.Device.CreateUnorderedAccessView(ssgiTarget.RenderTarget, null, new UnorderedAccessViewDescription
        {
            Format = Dx12OffscreenTarget.HdrFormat, ViewDimension = UnorderedAccessViewDimension.Texture2D,
        }, bindless.Cpu(RtGiTableBase + 5));

        ssgiTarget.ColorToUnorderedAccess();
        uint idSize = Vortice.Direct3D12.D3D12.ShaderIdentifierSizeInBytes;
        dev.ExecuteSync(cl =>
        {
            cl.SetDescriptorHeaps(bindless
                .Heap); // bindless heap = the bound CBV/SRV/UAV heap (table + ResourceDescriptorHeap[])
            cl.SetComputeRootSignature(rtGiRootSig);
            cl.SetPipelineState1(rtGiPso);
            cl.SetComputeRootConstantBufferView(0, rtGiCb.GPUVirtualAddress);
            cl.SetComputeRootConstantBufferView(1, rtGiSunCb.GPUVirtualAddress);
            cl.SetComputeRootDescriptorTable(2, bindless.Gpu(RtGiTableBase));
            cl.SetComputeRootShaderResourceView(3, gpuDriven.MaterialsGpuAddress); // t5 GpuMaterials
            cl.SetComputeRootShaderResourceView(4, rtGeometry.InstancesGpuAddress); // t6 RtInstance[]
            cl.SetComputeRootShaderResourceView(5, clusteredLights.LightBufGpuAddress); // t7 punctual lights
            cl.DispatchRays(new DispatchRaysDescription
            {
                Width = (uint)ssgiTarget.Width, Height = (uint)ssgiTarget.Height, Depth = 1,
                RayGenerationShaderRecord = new GpuVirtualAddressRange
                    { StartAddress = rtGiSbt.GPUVirtualAddress, SizeInBytes = idSize },
                MissShaderTable = new GpuVirtualAddressRangeAndStride
                {
                    StartAddress = rtGiSbt.GPUVirtualAddress + RtSbtSlot, SizeInBytes = idSize, StrideInBytes = idSize
                },
                HitGroupTable = new GpuVirtualAddressRangeAndStride
                {
                    StartAddress = rtGiSbt.GPUVirtualAddress + 2 * RtSbtSlot, SizeInBytes = idSize,
                    StrideInBytes = idSize
                },
            });
        });
        ssgiTarget.ColorToShaderResource();

        SsgiResolveAndCombine(ctx); // shared: motion temporal + OIDN + composite
    }

    // PHASE 4 (P4.0): the screen-space radiance probe final gather (Place → Trace → Blend → Integrate),
    // leaving the (raw, pre-exposed) GI in ssgiTarget for the shared SsgiResolveAndCombine. Called from DrawRtGi
    // only when ScreenProbeEnabled AND DDGI is on/allocated (the trace hands off to the DDGI world cache for its
    // far field). G-buffer depth is in NonPixelShaderResource on entry; albedo + normal in the combined
    // shader-read state from the deferred pass. invVP is UN-transposed inverse view-projection.
    unsafe void DrawScreenProbeGather(Dx12FrameContext ctx, Matrix4x4 invVP)
    {
        var dev = ctx.Dev;
        var gbuffer = ctx.GBuffer;
        var ibl = ctx.Ibl;
        var gpuDriven = ctx.GpuDriven;
        var clusteredLights = ctx.ClusteredLights;
        var sceneAS = ctx.Dxr.SceneAS;
        var rtGeometry = ctx.Dxr.RtGeometry;
        var ddgi = ctx.Dxr.Ddgi;
        if (screenProbe == null) screenProbe = new Dx12ScreenProbe(dev);
        screenProbe.EmissiveEnabled = GiEmissiveEnabled(ctx);
        screenProbe.EnsureAllocated(ssgiTarget.Width, ssgiTarget.Height);
        screenProbe.Build();

        // Per-frame constants: the screen-probe grid + the DDGI grid description the trace samples on miss.
        var ddgiGrid = ddgi.GridConstants();
        screenProbe.PrepareConstants(Matrix4x4.Transpose(invVP),
            maxRayDist: MathF.Max(ctx.PostFX.SsgiRayLength, 3f), // SHORT near/mid-field ray (DDGI handles far)
            preExposure: SsgiPreExposure(), intensity: MathF.Max(ctx.PostFX.SsgiIntensity, 0f),
            deterministic: ctx.DeterministicCapture, ddgiGrid);

        if (!screenProbeLogged)
        {
            screenProbeLogged = true;
            Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"[SCREENPROBE] grid {screenProbe.ProbesX}x{screenProbe.ProbesY}={screenProbe.ProbeCount} probes " +
                $"(1 per {Dx12ScreenProbe.Downsample}x{Dx12ScreenProbe.Downsample} px); {Dx12ScreenProbe.OctTexels}x{Dx12ScreenProbe.OctTexels} octahedral, " +
                $"{Dx12ScreenProbe.RaysPerProbe} rays/probe = {screenProbe.ProbeCount * Dx12ScreenProbe.RaysPerProbe} rays/frame; " +
                $"VRAM {screenProbe.GridVramBytes / (1024.0 * 1024.0):0.0} MB"));
        }

        var sw = GiTimingEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;

        // PLACE: probePos/probeNormal from the G-buffer (depth NonPixelSRV, normal = G-buffer RT1).
        screenProbe.DispatchPlace(gbuffer.DepthSrvCpu, gbuffer.ColorSrvCpu(1));

        // TRACE: write the 3-descriptor bindless tail block ([0] TLAS, [1] irr cube, [2] DDGI atlas), then
        // dispatch with the shared bindless geo/material addresses (same as the DDGI/RT-GI pass).
        Dx12DescriptorHeap bh = Dx12Backend.BindlessHeap;
        var ddgiHeapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        sceneAS.CreateTlasSrv(bh.Cpu(ScreenProbeTableBase + 0)); // t0 TLAS
        dev.Device.CopyDescriptorsSimple(1, bh.Cpu(ScreenProbeTableBase + 1), ibl.IrradianceSrv,
            ddgiHeapType); // t3 irr cube
        dev.Device.CreateShaderResourceView(ddgi.IrradianceTex, new ShaderResourceViewDescription
        {
            // t4 DDGI atlas
            Format = Format.R16G16B16A16_Float,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
        }, bh.Cpu(ScreenProbeTableBase + 2));
        // t11 (table offset +3) = DDGI DEPTH-moments atlas — the Chebyshev LEAK GATE for the far-field read
        // (SampleDdgiField). Without it the sealed-interior screen probes pulled sky-lit probes from OUTSIDE the
        // wall → a dark room lit by leaked sky. RG16F (mean, mean²).
        dev.Device.CreateShaderResourceView(ddgi.DepthTex, new ShaderResourceViewDescription
        {
            Format = Format.R16G16_Float,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
        }, bh.Cpu(ScreenProbeTableBase + 3));
        // Both DDGI atlases are in UnorderedAccess (left so by DispatchDdgi) → the trace reads them as NonPixelSRV.
        dev.ExecuteSync(cl =>
        {
            cl.ResourceBarrierTransition(ddgi.IrradianceTex, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            cl.ResourceBarrierTransition(ddgi.DepthTex, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
        });
        screenProbe.DispatchTrace(bh, bh.Gpu(ScreenProbeTableBase),
            rtGiSunCb.GPUVirtualAddress, gpuDriven.MaterialsGpuAddress, rtGeometry.InstancesGpuAddress,
            clusteredLights.LightBufGpuAddress, ddgi.ProbeStateGpuAddress);
        // Both DDGI atlases back to UnorderedAccess for next frame's DDGI blend.
        dev.ExecuteSync(cl =>
        {
            cl.ResourceBarrierTransition(ddgi.IrradianceTex, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
            cl.ResourceBarrierTransition(ddgi.DepthTex, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
        });

        // BLEND: rays → octahedral radiance tile (+ border).
        screenProbe.DispatchBlend();

        // INTEGRATE: full-res nearest-probe upsample → ssgiTarget (pre-exposed albedo*E).
        ssgiTarget.ColorToUnorderedAccess();
        screenProbe.DispatchIntegrate(gbuffer.DepthSrvCpu, gbuffer.ColorSrvCpu(1), gbuffer.ColorSrvCpu(0),
            ssgiTarget.RenderTarget, ssgiTarget.Width, ssgiTarget.Height);
        ssgiTarget.ColorToShaderResource();

        if (sw != null)
        {
            sw.Stop();
            ctx.Stats.GpuPasses.Add(("GI:ScreenProbe", sw.Elapsed.TotalMilliseconds));
        }
    }

    // ============================== build + helpers (moved from the orchestrator) ==============================

    // BuildSsgi moved VERBATIM. The 3 SSGI PSOs (gather/temporal/combine) share one rootsig + CB; the half-res
    // GI targets + history ping-pong + denoise scratch + the full-res scene scratch are allocated in Resize.
    unsafe void BuildSsgi()
    {
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0),
            ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 3, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var samp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        ssgiRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { samp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("Ssgi.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "Ssgi.hlsl");

        ID3D12PipelineState MakePso(string entry) => dev.Device.CreateGraphicsPipelineState(
            new GraphicsPipelineStateDescription
            {
                RootSignature = ssgiRootSig, VertexShader = vs,
                PixelShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, entry, "Ssgi.hlsl"),
                InputLayout = null, PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
                RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
                DepthStencilState = DepthStencilDescription.None,
                RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat }, DepthStencilFormat = Format.Unknown,
                SampleDescription = new SampleDescription(1, 0),
            });

        ssgiGatherPso = MakePso("PSGather");
        ssgiTemporalPso = MakePso("PSTemporal");
        ssgiCombinePso = MakePso("PSCombine");

        int cbSize = (Marshal.SizeOf<SsgiConstants>() + 255) & ~255;
        ssgiCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        ssgiCbMapped = ssgiCb.Map<byte>(0);
        // 3 SRVs each for gather + temporal + combine = 9 contiguous slots per frame.
        ssgiSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 9, shaderVisible: true,
            framesInFlight: dev.FramesInFlight);
    }

    // AllocSsgiTargets moved VERBATIM into Resize (graph.Resize fans this out in registration order, R5). Half-
    // res GI + history ping-pong + denoise scratch (allowUav — the RT-GI gather + OIDN unpack write via UAV);
    // full-res scene scratch. The OIDN GPU shared buffer re-sizes on the next OIDN use (size-change detected).
    public void Resize(int w, int h)
    {
        // V2: ssgiTarget/ssgiDenoised/ssgiScene are audit-passed transients (the gather/RT-dispatch fully writes
        // ssgiTarget before the resolve reads it; OIDN unpack fully overwrites ssgiDenoised; the combine fully
        // overwrites ssgiScene). ssgiHistoryA/B are CROSS-FRAME TEMPORAL history (the resolve reads histRead before
        // writing histWrite) → IMPORTED, NEVER pooled/aliased (aliasing history = temporal corruption). AllocOrPool
        // = committed when no pool (byte-identical), placed-aliased when active.
        // Dispose pooled fields unless pool-placed (the pool re-acquire disposes its own Live); history is always committed.
        if (ssgiTarget is { IsPlaced: false }) ssgiTarget.Dispose();
        if (ssgiScene is { IsPlaced: false }) ssgiScene.Dispose();
        if (ssgiDenoised is { IsPlaced: false }) ssgiDenoised.Dispose();
        ssgiHistoryA?.Dispose();
        ssgiHistoryB?.Dispose(); // history: always committed, dispose unconditionally
        int hw = Math.Max(1, w / 2), hh = Math.Max(1, h / 2);
        // allowUav so the RT-GI gather can write it via a UAV (the SSGI gather still uses the RTV).
        ssgiTarget = Dx12RenderTargetPool.AllocOrPool(dev, "ssgiTarget", hw, hh, Dx12OffscreenTarget.HdrFormat,
            colorReadable: true, allowUav: true);
        ssgiHistoryA = new Dx12OffscreenTarget(dev, hw, hh, withDepth: false, // IMPORTED history — never pooled
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
        ssgiHistoryB = new Dx12OffscreenTarget(dev, hw, hh, withDepth: false, // IMPORTED history — never pooled
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
        ssgiDenoised = Dx12RenderTargetPool.AllocOrPool(dev, "ssgiDenoised", hw, hh, Dx12OffscreenTarget.HdrFormat,
            colorReadable: true, allowUav: true); // OIDN GPU unpack writes it
        ssgiScene = Dx12RenderTargetPool.AllocOrPool(dev, "ssgiScene", w, h, Dx12OffscreenTarget.HdrFormat,
            colorReadable: true, allowUav: false);
        ssgiHistValid = false; // accumulated history is stale after a (re)allocation
        ssgiCpuColor = ssgiCpuOut = null; // host buffers re-size to the new half-res
    }

    // --- helpers replicated from the orchestrator (read env / ctx.PostFX; identical semantics) ---
    static float SsgiPreExposure() => float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_EXPOSURE"),
        System.Globalization.CultureInfo.InvariantCulture, out float e)
        ? e
        : 1.0e-5f;

    bool? giTimingOn;

    bool GiTimingEnabled => giTimingOn ??= (Environment.GetEnvironmentVariable("BALLISTIC_DX12_GI_TIMING") == "1"
                                            || !string.IsNullOrWhiteSpace(
                                                Environment.GetEnvironmentVariable("BALLISTIC_STATS_OUT")));

    // GI-ISOLATE debug view: the combine outputs ONLY the indirect bounce. Env door OR the volume's SsgiDebugView.
    static bool GiIsolateOn(Dx12FrameContext ctx) =>
        Environment.GetEnvironmentVariable("BALLISTIC_DX12_GI_ISOLATE") == "1" || ctx.PostFX.SsgiDebugView;

    // DDGI world cache: volume PostFX.Ddgi, force-overridden by the BALLISTIC_DX12_DDGI env door. Door read once.
    string ddgiEnvCached;
    bool ddgiEnvRead;

    bool DdgiEnabled(Dx12FrameContext ctx)
    {
        if (!ddgiEnvRead)
        {
            ddgiEnvCached = Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI");
            ddgiEnvRead = true;
        }

        return ddgiEnvCached is null ? ctx.PostFX.Ddgi : ddgiEnvCached == "1";
    }

    // Screen probes: volume PostFX.ScreenProbes, force-overridden by BALLISTIC_DX12_SCREENPROBE ("0" off). Once.
    string screenProbeEnvCached;
    bool screenProbeEnvRead;

    bool ScreenProbeEnabled(Dx12FrameContext ctx)
    {
        if (!screenProbeEnvRead)
        {
            screenProbeEnvCached = Environment.GetEnvironmentVariable("BALLISTIC_DX12_SCREENPROBE");
            screenProbeEnvRead = true;
        }

        return screenProbeEnvCached is null ? ctx.PostFX.ScreenProbes : screenProbeEnvCached != "0";
    }

    // Emissive-as-GI-source: volume PostFX.GiEmissive, force-overridden by BALLISTIC_DX12_GI_EMISSIVE ("0" off). Once.
    string giEmissiveEnvCached;
    bool giEmissiveEnvRead;

    bool GiEmissiveEnabled(Dx12FrameContext ctx)
    {
        if (!giEmissiveEnvRead)
        {
            giEmissiveEnvCached = Environment.GetEnvironmentVariable("BALLISTIC_DX12_GI_EMISSIVE");
            giEmissiveEnvRead = true;
        }

        return giEmissiveEnvCached is null ? ctx.PostFX.GiEmissive : giEmissiveEnvCached != "0";
    }

    public void Dispose()
    {
        ssgiGatherPso?.Dispose();
        ssgiTemporalPso?.Dispose();
        ssgiCombinePso?.Dispose();
        ssgiRootSig?.Dispose();
        ssgiCb?.Dispose();
        ssgiSrvVisible?.Dispose();
        ssgiTarget?.Dispose();
        ssgiHistoryA?.Dispose();
        ssgiHistoryB?.Dispose();
        ssgiDenoised?.Dispose();
        ssgiScene?.Dispose();
        ssgiOidn?.Dispose();
        ssgiOidnGpu?.Dispose();
        rtGiPso?.Dispose();
        rtGiRootSig?.Dispose();
        rtGiSbt?.Dispose();
        rtGiCb?.Dispose();
        rtGiSunCb?.Dispose();
        rtGiHeap?.Dispose();
        screenProbe?.Dispose(); // ddgi lives in the shared holder (ctx.Dxr) — disposed there.
        ddgiFar?.Dispose(); ddgiFar = null;   // CHUNK3 far cascade is owned here (not in ctx.Dxr)
    }
}