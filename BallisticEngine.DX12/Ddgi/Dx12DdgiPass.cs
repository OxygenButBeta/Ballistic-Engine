using System;
using System.Numerics;
using System.Runtime.InteropServices;
using BallisticEngine;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// DDGI — the single product-facing GI pass (event GlobalIllumination = 500, the slot the legacy Lumen pass
// held). World-space irradiance probe grid; replaces Lumen V2 with ONE predictable feedback loop:
//
//   1. Relight  (compute)  per-probe RT trace → shade hits (sun+shadow-ray + punctual + emissive) + sky on a
//                          miss → integrate into the probe's octahedral irradiance cell, EMA over the previous
//                          frame. View-independent: no reprojection, no motion vectors.
//   2. Sample   (compute)  per full-res pixel: trilinear-gather the 8 bracketing probes → indirect E.
//   3. Combine  (PS)       E*albedo*ao/PI added into the HDR color (One/One). Deferred already suppressed its
//                          IBL diffuse ambient (ctx.GiActiveThisFrame) → no double count.
//
// No screen-space temporal / SVGF / async double-buffer / per-pixel trace — the ghosting/disocclusion class is
// gone (the cache is world-space). HW-RT only. Default-off = no-op, byte-identical no-GI frame.
public sealed class Dx12DdgiPass : IRenderPass, IDisposable
{
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.GlobalIllumination;
    public string Name => "DDGI";

    readonly Dx12Device dev;
    readonly Dx12DdgiProbeGrid grid;
    public Dx12DdgiProbeGrid Grid => grid;

    // Occupancy-aware probe placement (relocation + classification) — lazily created, shares the DDGI scene AS
    // (same TLAS the relight traces) so it needs no second AS build. The query does a CPU readback, so it MUST run
    // with the pipelined frame CLOSED: Record (inside the open frame) only ARMS placementPending, and the renderer
    // drains it via RunPendingPlacement() at the next BeginRender before dev.BeginFrame() (frame still closed).
    GpuSceneQuery placementQuery;
    bool placementPending;
    Dx12SceneAS lastSceneAS;   // the TLAS DDGI built last Record — the deferred placement traces it

    // ---- relight (per-probe RT trace) ----
    ID3D12RootSignature relightRootSig;
    ID3D12PipelineState relightPso;
    Dx12FrameCb<RelightConstants> relightCb;
    const int RelightSkyTableBase = Dx12BindlessTail.DdgiRelightTableBase;   // t5 sky cube
    const int RelightRays = 64;   // must match DdgiRelight.hlsl RAYS

    // ---- sample (full-res gather) ----
    ID3D12RootSignature sampleRootSig;
    ID3D12PipelineState samplePso;
    Dx12FrameCb<SampleConstants> sampleCb;
    Dx12DescriptorHeap sampleSrv;   // per pass: depth SRV + normal SRV + Indirect UAV (3)
    Dx12OffscreenTarget indirect;   // full-res RGBA16F incoming irradiance E

    // ---- A3: spatial denoise (compute, between Sample and Combine) ----
    ID3D12RootSignature denoiseRootSig;
    ID3D12PipelineState denoisePso;
    Dx12FrameCb<DenoiseConstants> denoiseCb;
    Dx12DescriptorHeap denoiseSrv;  // per pass: Indirect SRV + depth SRV + normal SRV + SSAO SRV + Filtered UAV (5)
    Dx12OffscreenTarget indirectFiltered; // full-res RGBA16F denoised E (Combine reads this when denoise ran)
    bool denoisedThisFrame;

    // ---- A4: near-field SSGI complement (compute, reads current SceneColor; contact GI / crevice the coarse
    // probes can't resolve). Spatial-only, no history. ----
    ID3D12RootSignature nearFieldRootSig;
    ID3D12PipelineState nearFieldPso;
    Dx12FrameCb<NearFieldConstants> nearFieldCb;
    Dx12DescriptorHeap nearFieldSrv;   // depth SRV + normal SRV + SceneColor SRV + NearField UAV (4)
    Dx12OffscreenTarget nearField;     // full-res RGBA16F: rgb = near-field GI radiance, a = coverage
    bool nearFieldThisFrame;

    // ---- combine (additive fullscreen) ----
    ID3D12RootSignature combineRootSig;
    ID3D12PipelineState combinePso, combineDebugPso;
    Dx12FrameCb<CombineConstants> combineCb;
    Dx12DescriptorHeap combineSrv;  // per pass: Indirect SRV + albedo SRV + AO SRV (3)

    [StructLayout(LayoutKind.Sequential)]
    struct RelightConstants
    {
        public Vector3 GridOrigin;   public float RayCount;
        public Vector3 ProbeSpacing; public float SkyIntensity;
        public uint CountX, CountY, CountZ; public float UseSky;
        public Vector3 SunDir;       public float SunBias;
        public Vector3 SunColor;     public float LightCount;
        public float EmaAlpha;       public float HistoryValid; public float Intensity; public float FrameJitter;
        public float MultiBounce;    public float BounceBoost;  public float UsePlacement; public float Pad1;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SampleConstants
    {
        public Matrix4x4 InvViewProj;
        public Vector3 GridOrigin;   public float Pad0;
        public Vector3 ProbeSpacing; public float NormalBias;
        public uint CountX, CountY, CountZ; public uint W;
        public uint H; public float Intensity; public float UseVisibility; public float UsePlacement;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CombineConstants { public float AoStrength; public float Intensity; public float UseNearField; public float NearFieldBlend; }

    [StructLayout(LayoutKind.Sequential)]
    struct DenoiseConstants
    {
        public uint W, H; public float UseSsao; public float FrameIndex;   // FrameIndex<0 = deterministic (fixed spiral)
        public float Strength; public float Pad0, Pad1, Pad2;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NearFieldConstants
    {
        public Matrix4x4 InvProjection;
        public Matrix4x4 Projection;
        public Matrix4x4 View;
        public uint W, H; public float Radius; public float FrameIndex;
        public float SliceCount; public float StepCount; public float Intensity; public float Thickness;
    }

    // ---- debug probe overlay (BALLISTIC_DX12_DDGI_DEBUG_PROBES=1) ----
    ID3D12RootSignature debugRootSig;
    ID3D12PipelineState debugPso;
    Dx12FrameCb<DebugConstants> debugCb;

    [StructLayout(LayoutKind.Sequential)]
    struct DebugConstants
    {
        public Matrix4x4 ViewProj;
        public Vector3 GridOrigin;   public float ProbeRadius;
        public Vector3 ProbeSpacing; public float Pad0;
        public Vector3 CameraRight;  public float Pad1;
        public Vector3 CameraUp;     public float Pad2;
        public uint CountX, CountY, CountZ; public uint Pad3;
    }

    public Dx12DdgiPass(Dx12Device device, int width, int height)
    {
        dev = device;
        grid = new Dx12DdgiProbeGrid(device);
        BuildPipelines();
        Resize(width, height);
    }

    // ---- product door ----
    static int envDoor = -2;
    static bool Armed(Dx12FrameContext ctx)
    {
        if (envDoor == -2)
        {
            string v = Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI");
            envDoor = v == "1" ? 1 : v == "0" ? 0 : -1;
        }
        return envDoor == 1 || (envDoor == -1 && ctx.PostFX.DdgiEnabled);
    }

    public static bool WouldRun(Dx12FrameContext ctx) =>
        !ctx.Doors.Minimal && Armed(ctx) && ctx.Dev.HasHardwareRayTracing && ctx.Dxr?.SceneAS != null;

    public bool Enabled(Dx12FrameContext ctx)
    {
        bool run = WouldRun(ctx);
        // When GI is inactive the graph skips Record entirely, so the probe cache would freeze at its last
        // (possibly stale/over-bright) state and snap back the instant GI is re-enabled. Invalidate the history
        // here (Enabled is called every frame by the graph) so a re-enable rebuilds the cache clean — full
        // replace, no EMA over stale data. Cheap flag; no-op while GI stays on. No dependency on the orchestrator.
        if (!run) grid.ResetHistory();
        return run;
    }

    // Occupancy-aware placement door: BALLISTIC_DX12_DDGI_NOPLACEMENT=1 disables relocation/classification
    // (probes stay on the raw lattice — the pre-placement behaviour, for A/B). Default ON.
    static bool placementEnabled = Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_NOPLACEMENT") != "1";

    int gridX, gridY, gridZ;
    bool useVolumeBounds;
    Vector3 boundsMin, boundsMax;
    // Resolve the probe grid resolution: the GI volume (PostFX) drives it; BALLISTIC_DX12_DDGI_GRID="XxYxZ"
    // overrides for A/B. Read per-frame so a volume/quality-tier change takes effect live.
    void ReadGrid(Dx12FrameContext ctx)
    {
        gridX = Math.Max(2, ctx.PostFX.DdgiGridX);
        gridY = Math.Max(2, ctx.PostFX.DdgiGridY);
        gridZ = Math.Max(2, ctx.PostFX.DdgiGridZ);
        string v = Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_GRID");
        if (!string.IsNullOrEmpty(v))
        {
            string[] p = v.Split('x', 'X', '*', ',');
            if (p.Length == 3 && int.TryParse(p[0], out int x) && int.TryParse(p[1], out int y) && int.TryParse(p[2], out int z)
                && x > 0 && y > 0 && z > 0)
            { gridX = x; gridY = y; gridZ = z; }
        }

        // Volume bounds: confine the grid to the GI volume's box (mode 1) when it has a real box (extent > 0 on all
        // axes — a global volume reports extent 0 → fall back to the scene AABB). BALLISTIC_DX12_DDGI_BOUNDS=0 forces
        // the scene-AABB path for A/B. A static box → static grid → the cache converges (no per-frame re-fit).
        Vector3 e = ctx.PostFX.DdgiBoundsExtent;
        Vector3 c = ctx.PostFX.DdgiBoundsCenter;
        useVolumeBounds = ctx.PostFX.DdgiBoundsMode == 1 && e.X > 1e-3f && e.Y > 1e-3f && e.Z > 1e-3f
                          && Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_BOUNDS") != "0";
        // Diagnostic / A-B override: BALLISTIC_DX12_DDGI_TESTBOX="cx,cy,cz,ex,ey,ez" forces a volume box without a
        // scene Volume (lets the bounds path be verified on any scene). Real use drives it from the GI volume.
        string tb = Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_TESTBOX");
        if (!string.IsNullOrEmpty(tb))
        {
            string[] p = tb.Split(',');
            if (p.Length == 6 && float.TryParse(p[0], System.Globalization.CultureInfo.InvariantCulture, out float cx)
                && float.TryParse(p[1], System.Globalization.CultureInfo.InvariantCulture, out float cy)
                && float.TryParse(p[2], System.Globalization.CultureInfo.InvariantCulture, out float cz)
                && float.TryParse(p[3], System.Globalization.CultureInfo.InvariantCulture, out float ex)
                && float.TryParse(p[4], System.Globalization.CultureInfo.InvariantCulture, out float ey)
                && float.TryParse(p[5], System.Globalization.CultureInfo.InvariantCulture, out float ez))
            { c = new Vector3(cx, cy, cz); e = new Vector3(ex, ey, ez); useVolumeBounds = true; }
        }
        if (useVolumeBounds) { boundsMin = c - e; boundsMax = c + e; }
    }

    static float EnvF(string name, float fallback)
    {
        string v = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrEmpty(v) && float.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : fallback;
    }

    int frameCounter;
    Vector3 prevSunDir = new(float.NaN, 0, 0);   // NaN → first frame counts as a light change
    Vector3 prevSunColor;

    public void Resize(int width, int height)
    {
        indirect?.Dispose();
        indirect = new Dx12OffscreenTarget(dev, width, height, colorFormat: Dx12OffscreenTarget.HdrFormat,
            colorReadable: true, allowUav: true);
        // A3: spatial-denoise output (full-res RGBA16F). Combine reads THIS when the denoiser ran; otherwise it
        // reads `indirect` directly (door off → byte-identical). Pass-owned, not pooled (it's a per-pass scratch
        // but lives for the frame between Denoise and Combine, and persists across frames like `indirect`).
        indirectFiltered?.Dispose();
        indirectFiltered = new Dx12OffscreenTarget(dev, width, height, colorFormat: Dx12OffscreenTarget.HdrFormat,
            colorReadable: true, allowUav: true);
        // A4: near-field SSGI target (full-res RGBA16F; rgb = contact GI contribution, a = coverage).
        nearField?.Dispose();
        nearField = new Dx12OffscreenTarget(dev, width, height, colorFormat: Dx12OffscreenTarget.HdrFormat,
            colorReadable: true, allowUav: true);
    }

    public unsafe void Record(Dx12FrameContext ctx)
    {
        ReadGrid(ctx);
        frameCounter++;

        // Build/refresh the shared TLAS (DDGI may be the first RT effect in the frame — RT shadows/reflections
        // can be off). Stamp-cached: a static scene builds once. Without this the AS is never Valid → no-op.
        var sceneAS = ctx.Dxr.SceneAS;
        sceneAS.Ensure(ctx.WholeMeshRenderers);

        if (!grid.Ensure(ctx, gridX, gridY, gridZ, useVolumeBounds, boundsMin, boundsMax)) return;

        var rtGeo = ctx.Dxr.RtGeometry;
        // Ensure the bindless material table + per-instance geo SRVs are fresh (stamp-cached no-ops if a prior
        // RT pass already built them this frame).
        ctx.GpuDriven.EnsureMaterialTable(ctx.WholeMeshRenderers);
        rtGeo.Ensure(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection, ctx.GpuDriven);
        if (!rtGeo.Valid) return;

        if (!logged) { logged = true; Console.WriteLine($"    DDGI [GlobalIllumination=500] {grid.CountX}x{grid.CountY}x{grid.CountZ}={grid.ProbeCount} probes"); }

        // Occupancy-aware placement uses a CPU-readback GpuSceneQuery (Map) + an upload — which MUST run with the
        // pipelined frame list CLOSED (an open-frame ExecuteSync only records, so the readback would read garbage
        // and desync the fence → device removed). Record runs INSIDE the open frame, so we can't do it here: just
        // arm it. The renderer drains it via RunPendingPlacement() at the next BeginRender, BEFORE dev.BeginFrame().
        // The TLAS this frame built (sceneAS, stamp-cached) stays valid then, so the deferred placement traces it.
        if (placementEnabled && !grid.StatePlaced) { placementPending = true; lastSceneAS = sceneAS; }

        Relight(ctx, sceneAS, rtGeo);
        Sample(ctx);
        NearField(ctx);   // A4: reads SceneColor (lit, pre-DDGI-combine) → near-field one-bounce GI
        Denoise(ctx);
        Combine(ctx);
        // Probe-sphere debug overlay: GiVolume.debugProbes toggle OR the env door. BALLISTIC_DX12_DDGI_DEBUG_PROBES=0
        // FORCE-disables it (overrides the volume) so a headless capture can see the real render even when the scene's
        // GI volume left the overlay on.
        string probesEnv = Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_DEBUG_PROBES");
        if (probesEnv != "0" && (ctx.PostFX.DdgiDebugProbes || probesEnv == "1"))
            DrawProbes(ctx);
    }

    bool logged;

    // Drain a pending occupancy-aware placement. MUST be called with the pipelined frame CLOSED (the renderer
    // calls it at BeginRender BEFORE dev.BeginFrame()) — the query does a CPU readback + the upload waits on a
    // fence, both of which need a real submit, not an open-frame record. Cheap no-op when nothing is pending
    // (StatePlaced gates the actual work to once per grid layout). Idempotent + safe to call every frame.
    public void RunPendingPlacement()
    {
        if (!placementPending || grid.StatePlaced || lastSceneAS == null) return;
        if (!lastSceneAS.Valid) return;   // TLAS not ready (scene swap mid-flight) — retry next frame
        placementQuery ??= new GpuSceneQuery(dev, lastSceneAS, trustSharedScene: true);
        grid.PlaceProbes(dev, placementQuery);
        placementPending = false;
    }

    // Debug overlay: draw every probe as a small world-space sphere tinted by its irradiance, depth-tested
    // against the scene. Instanced billboard (6 verts × ProbeCount). Opt-in, after combine.
    unsafe void DrawProbes(Dx12FrameContext ctx)
    {
        var target = ctx.SceneColor;
        var gbuffer = ctx.GBuffer;

        // Camera right/up from the view matrix rows (the billboard faces the camera).
        Matrix4x4 v = ctx.View;
        Vector3 camRight = new(v.M11, v.M21, v.M31);
        Vector3 camUp = new(v.M12, v.M22, v.M32);
        float radius = 0.25f * MathF.Min(grid.ProbeSpacing.X, MathF.Min(grid.ProbeSpacing.Y, grid.ProbeSpacing.Z));
        radius = MathF.Max(radius, 0.05f);

        debugCb.Write(new DebugConstants
        {
            ViewProj = Matrix4x4.Transpose(ctx.ViewProj),
            GridOrigin = grid.GridOrigin, ProbeRadius = radius,
            ProbeSpacing = grid.ProbeSpacing,
            CameraRight = camRight, CameraUp = camUp,
            CountX = (uint)grid.CountX, CountY = (uint)grid.CountY, CountZ = (uint)grid.CountZ,
        });

        var irrad = grid.IrradianceRead;   // this frame's irradiance (post-swap)
        gbuffer.DepthToReadOnly();          // depth as a DSV the overlay tests against (no write)

        target.RenderColorWithExternalDepth(gbuffer.DsvHandle, cl =>
        {
            cl.SetGraphicsRootSignature(debugRootSig);
            cl.SetPipelineState(debugPso);
            cl.SetGraphicsRootConstantBufferView(0, debugCb.Gpu);
            cl.SetGraphicsRootShaderResourceView(1, irrad.GPUVirtualAddress);
            cl.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
            cl.DrawInstanced(6, (uint)grid.ProbeCount, 0, 0);
        });
    }

    // ---- Pass 1: per-probe relight ----
    unsafe void Relight(Dx12FrameContext ctx, Dx12SceneAS sceneAS, Dx12RtGeometry rtGeo)
    {
        Vector3 sunDir = ctx.LightDir.LengthSquared() < 1e-8f ? Vector3.UnitY : Vector3.Normalize(ctx.LightDir);
        // Sky on a probe-ray miss only when the IBL is actually BAKED. ctx.Ibl is a valid object even with no Sky
        // component in the scene, but its env cube is then UNBAKED (black) — sampling it added nothing but also
        // meant the "useSky" flag lied. Gate on HasBaked so a sky-less closed room correctly contributes zero on
        // a miss (the indirect then comes purely from lit surface hits — point/sun light bounces).
        bool useSky = ctx.Ibl != null && ctx.Ibl.HasBaked;
        float intensity = EnvF("BALLISTIC_DX12_DDGI_INTENSITY", ctx.PostFX.DdgiIntensity);
        float ema = EnvF("BALLISTIC_DX12_DDGI_ALPHA", ctx.PostFX.DdgiEmaAlpha);
        // Under a deterministic capture the per-frame jitter must be fixed (golden byte-identical) AND the EMA
        // history must not change frame-to-frame → full replace (HistoryValid 0).
        bool det = ctx.DeterministicCapture;

        // HYSTERESIS EMA (D4): when the sun direction/color changes a lot, blend the new radiance in fast (the
        // old cache is stale); when the scene is settled, blend slowly (low noise). A static light → the cache
        // converges then sits at the low alpha. Off under a deterministic capture (fixed sun, byte-stable).
        bool lightChanged = !det && (Vector3.DistanceSquared(prevSunDir, sunDir) > 1e-6f
                                     || Vector3.DistanceSquared(prevSunColor, ctx.LightColor) > 1e-4f);
        prevSunDir = sunDir; prevSunColor = ctx.LightColor;
        if (lightChanged) ema = MathF.Max(ema, 0.5f);   // snap toward the new lighting

        // ROTATED ray set (live path): each frame aims a different 64-ray Fibonacci rotation; the low EMA
        // integrates them over time → true Monte-Carlo convergence with NO fixed-set bias, yet flicker-free on a
        // static scene (the integral averages instead of jumping). With rotation the per-frame estimate is noisy,
        // so cap the EMA LOW (a high alpha would let one noisy frame flash through). Deterministic capture keeps a
        // fixed rotation (FrameJitter -1) + full replace for byte-stable goldens. Hysteresis still wins on a light
        // change. The probe count gives the rotation index (wraps; varies the rotation without an RNG).
        float frameJitter = det ? -1f : (frameCounter & 1023);
        // Settled + rotating-ray-set: the per-frame 64-ray estimate is noisy (a different Fibonacci rotation each
        // frame), so the EMA must blend it in SLOWLY or the noise never averages out — it just slides across the
        // surface frame to frame (the "perlin/sin-wave creeping darkness" the user saw). RTXGI's hysteresis is
        // ~0.97 (alpha ~0.03); the old 0.12 cap let 12% of each noisy frame through → visible crawling noise. Drop
        // to 0.03 so a static scene converges to a stable, smooth result. Hysteresis still snaps to 0.5 on a real
        // lighting change (above), so responsiveness is unaffected.
        if (!det && !lightChanged) ema = MathF.Min(ema, 0.03f);

        relightCb.Write(new RelightConstants
        {
            GridOrigin = grid.GridOrigin, RayCount = RelightRays,
            ProbeSpacing = grid.ProbeSpacing, SkyIntensity = EnvF("BALLISTIC_DX12_DDGI_SKY", ctx.PostFX.DdgiSkyIntensity),
            CountX = (uint)grid.CountX, CountY = (uint)grid.CountY, CountZ = (uint)grid.CountZ,
            UseSky = useSky ? 1f : 0f,
            SunDir = sunDir, SunBias = 0.05f,
            SunColor = ctx.LightColor, LightCount = ctx.ClusteredLights.LightCount,
            EmaAlpha = ema, HistoryValid = (grid.HistoryValid && !det) ? 1f : 0f,
            Intensity = intensity, FrameJitter = frameJitter,
            MultiBounce = Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_NOBOUNCE") == "1" ? 0f
                          : (ctx.PostFX.DdgiMultiBounce ? 1f : 0f),
            BounceBoost = EnvF("BALLISTIC_DX12_DDGI_BOUNCE_BOOST", 1f),
            UsePlacement = (placementEnabled && grid.StatePlaced) ? 1f : 0f,
        });

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Dx12DescriptorHeap bindless = Dx12Backend.BindlessHeap;
        // Bind the RADIANCE env cube (NOT the irradiance cube): each probe ray samples sky RADIANCE in its
        // direction, and the per-probe cosine integration over the 64 rays produces the irradiance. Sampling the
        // already-cosine-convolved irradiance cube per ray and integrating AGAIN double-convolves it → ~π× energy
        // loss → the GI sky ambient came out far too dark (the "GI darkens instead of lights" report).
        dev.Device.CopyDescriptorsSimple(1, bindless.Cpu(RelightSkyTableBase + 0), ctx.Ibl.EnvSrv, heapType);

        var irradW = grid.IrradianceWrite;
        var irradR = grid.IrradianceRead;
        var visW = grid.VisibilityWrite;
        var visR = grid.VisibilityRead;
        dev.ExecuteSync(cl =>
        {
            void ToState(ID3D12Resource r, ResourceStates s) { if (grid.StateOf(r) != s) { cl.ResourceBarrierTransition(r, grid.StateOf(r), s); grid.SetState(r, s); } }
            ToState(irradW, ResourceStates.UnorderedAccess);
            ToState(visW, ResourceStates.UnorderedAccess);
            ToState(irradR, ResourceStates.NonPixelShaderResource);
            ToState(visR, ResourceStates.NonPixelShaderResource);

            cl.SetDescriptorHeaps(bindless.Heap);
            cl.SetComputeRootSignature(relightRootSig);
            cl.SetPipelineState(relightPso);
            cl.SetComputeRootConstantBufferView(0, relightCb.Gpu);
            cl.SetComputeRootShaderResourceView(1, sceneAS.TlasAddress);                  // t0 TLAS
            cl.SetComputeRootUnorderedAccessView(2, grid.IrradianceWriteGpu);             // u0 Irradiance
            cl.SetComputeRootShaderResourceView(3, grid.IrradianceReadGpu);               // t1 PrevIrrad
            cl.SetComputeRootShaderResourceView(4, rtGeo.InstancesGpuAddress);            // t2 RtInstance[]
            cl.SetComputeRootShaderResourceView(5, ctx.GpuDriven.MaterialsGpuAddress);    // t3 GpuMaterials
            cl.SetComputeRootShaderResourceView(6, ctx.ClusteredLights.LightBufGpuAddress); // t4 Lights
            cl.SetComputeRootDescriptorTable(7, bindless.Gpu(RelightSkyTableBase));       // t5 sky cube
            cl.SetComputeRootUnorderedAccessView(8, grid.VisibilityWriteGpu);             // u1 Visibility
            cl.SetComputeRootShaderResourceView(9, grid.VisibilityReadGpu);               // t6 PrevVis
            cl.SetComputeRootShaderResourceView(10, grid.ProbeStateGpu);                  // t7 ProbeState
            cl.Dispatch((uint)grid.ProbeCount, 1, 1);                                     // one GROUP per probe
            cl.ResourceBarrierTransition(irradW, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            cl.ResourceBarrierTransition(visW, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
        });
        grid.SetState(irradW, ResourceStates.NonPixelShaderResource);
        grid.SetState(visW, ResourceStates.NonPixelShaderResource);
        grid.SwapAndMarkHistory();
    }

    // ---- Pass 2: full-res sample ----
    unsafe void Sample(Dx12FrameContext ctx)
    {
        var gbuffer = ctx.GBuffer;
        Matrix4x4.Invert(ctx.ViewProj, out Matrix4x4 invVP);
        // NOTE: the relight just swapped the ping-pong, so the buffer we want to READ (this frame's freshly
        // written irradiance) is now IrradianceRead.
        var irrad = grid.IrradianceRead;

        sampleCb.Write(new SampleConstants
        {
            InvViewProj = Matrix4x4.Transpose(invVP),
            GridOrigin = grid.GridOrigin, ProbeSpacing = grid.ProbeSpacing,
            NormalBias = EnvF("BALLISTIC_DX12_DDGI_NORMALBIAS", ctx.PostFX.DdgiNormalBias),
            CountX = (uint)grid.CountX, CountY = (uint)grid.CountY, CountZ = (uint)grid.CountZ,
            W = (uint)indirect.Width, H = (uint)indirect.Height,
            // Intensity = user DISPLAY gain, applied on the final gather ONLY (not baked into the stored irradiance —
            // that fed the multi-bounce loop an Intensity× gain every frame → runaway blow-out).
            Intensity = EnvF("BALLISTIC_DX12_DDGI_INTENSITY", ctx.PostFX.DdgiIntensity),
            UseVisibility = (Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_NOVIS") == "1" || !ctx.PostFX.DdgiVisibility) ? 0f : 1f,
            UsePlacement = (placementEnabled && grid.StatePlaced) ? 1f : 0f,
        });

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        // table: depth SRV (t0), normal SRV (t1), Indirect UAV (u0), albedo SRV (t5)
        dev.Device.CopyDescriptorsSimple(1, sampleSrv.Cpu(0), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, sampleSrv.Cpu(1), gbuffer.ColorSrvCpu(1), heapType);
        // Indirect UAV — create into slot 2.
        dev.Device.CreateUnorderedAccessView(indirect.RenderTarget, null,
            new UnorderedAccessViewDescription { ViewDimension = UnorderedAccessViewDimension.Texture2D, Format = Dx12OffscreenTarget.HdrFormat },
            sampleSrv.Cpu(2));
        dev.Device.CopyDescriptorsSimple(1, sampleSrv.Cpu(3), gbuffer.ColorSrvCpu(0), heapType);   // t5 albedo (G0)

        // Depth → NonPixel for the compute read. The G-buffer colors arrive in the combined ShaderRead state
        // (Pixel|NonPixel) from the deferred pass (event 300 < 500), so the normal SRV (G1) is already readable
        // from compute — no extra color transition needed.
        gbuffer.DepthToNonPixelShaderResource();
        indirect.ColorToUnorderedAccess();

        dev.ExecuteSync(cl =>
        {
            cl.SetDescriptorHeaps(sampleSrv.Heap);
            cl.SetComputeRootSignature(sampleRootSig);
            cl.SetPipelineState(samplePso);
            cl.SetComputeRootConstantBufferView(0, sampleCb.Gpu);
            cl.SetComputeRootShaderResourceView(1, irrad.GPUVirtualAddress);              // t2 Irradiance (root SRV)
            cl.SetComputeRootShaderResourceView(2, grid.VisibilityRead.GPUVirtualAddress); // t3 VisMoments (root SRV)
            cl.SetComputeRootShaderResourceView(3, grid.ProbeStateGpu);                   // t4 ProbeState (root SRV)
            cl.SetComputeRootDescriptorTable(4, sampleSrv.Gpu(0));                        // t0 depth, t1 normal, u0 Indirect
            cl.Dispatch((uint)((indirect.Width + 7) / 8), (uint)((indirect.Height + 7) / 8), 1);
        });
    }

    // ---- A4: near-field SSGI complement (radiance-carrying horizon march on the current SceneColor) ----
    unsafe void NearField(Dx12FrameContext ctx)
    {
        nearFieldThisFrame = false;
        // Door BALLISTIC_DX12_DDGI_NEARFIELD (default ON; =0 = skip → no near-field, byte-identical pre-A4).
        string env = Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_NEARFIELD");
        if (env == "0") return;
        float intensity = EnvF("BALLISTIC_DX12_DDGI_NEARFIELD_INTENSITY", 1f);
        if (intensity <= 0f) return;

        bool det = ctx.DeterministicCapture;
        var gbuffer = ctx.GBuffer;
        var scene = ctx.SceneColor;
        Matrix4x4.Invert(ctx.Proj, out Matrix4x4 invProj);

        nearFieldCb.Write(new NearFieldConstants
        {
            InvProjection = Matrix4x4.Transpose(invProj),
            Projection = Matrix4x4.Transpose(ctx.Proj),
            View = Matrix4x4.Transpose(ctx.View),
            W = (uint)nearField.Width, H = (uint)nearField.Height,
            Radius = EnvF("BALLISTIC_DX12_DDGI_NEARFIELD_RADIUS", 0.8f),   // world metres — contact/crevice scale
            FrameIndex = det ? -1f : (frameCounter & 1023),
            SliceCount = 3f, StepCount = 6f,                              // half-budget march (realtime); TAA integrates
            Intensity = intensity, Thickness = 0.5f,
        });

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        // t0 depth, t1 normal (G1), t2 SceneColor (lit HDR), u0 NearField. All COMPUTE reads → non-pixel state.
        gbuffer.DepthToNonPixelShaderResource();
        scene.ColorToNonPixelShaderResource();
        nearField.ColorToUnorderedAccess();
        dev.Device.CopyDescriptorsSimple(1, nearFieldSrv.Cpu(0), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, nearFieldSrv.Cpu(1), gbuffer.ColorSrvCpu(1), heapType);  // t1 normal (G1)
        dev.Device.CopyDescriptorsSimple(1, nearFieldSrv.Cpu(2), scene.ColorSrvCpu, heapType);       // t2 SceneColor
        dev.Device.CopyDescriptorsSimple(1, nearFieldSrv.Cpu(3), gbuffer.ColorSrvCpu(0), heapType);  // t3 albedo (G0)
        dev.Device.CreateUnorderedAccessView(nearField.RenderTarget, null,
            new UnorderedAccessViewDescription { ViewDimension = UnorderedAccessViewDimension.Texture2D, Format = Dx12OffscreenTarget.HdrFormat },
            nearFieldSrv.Cpu(4));

        dev.ExecuteSync(cl =>
        {
            cl.SetDescriptorHeaps(nearFieldSrv.Heap);
            cl.SetComputeRootSignature(nearFieldRootSig);
            cl.SetPipelineState(nearFieldPso);
            cl.SetComputeRootConstantBufferView(0, nearFieldCb.Gpu);
            cl.SetComputeRootDescriptorTable(1, nearFieldSrv.Gpu(0));
            cl.Dispatch((uint)((nearField.Width + 7) / 8), (uint)((nearField.Height + 7) / 8), 1);
        });
        // Restore SceneColor to a render-target state so Combine can additively blend into it.
        scene.ColorToRenderTarget();
        nearFieldThisFrame = true;
    }

    // ---- A3: spatial denoise (variance/validity-driven adaptive à-trous; spatial-only, no temporal feedback) ----
    unsafe void Denoise(Dx12FrameContext ctx)
    {
        denoisedThisFrame = false;
        // Door: BALLISTIC_DX12_DDGI_DENOISE (default ON; =0 = skip → Combine reads `indirect` directly, byte-id to
        // pre-A3). Strength scales the max blur radius; 0 also disables. Deterministic capture KEEPS the denoise on
        // (it's spatial, fully deterministic) but with a fixed spiral (FrameIndex<0) so goldens stay byte-stable.
        string env = Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_DENOISE");
        if (env == "0") return;
        float strength = EnvF("BALLISTIC_DX12_DDGI_DENOISE_STRENGTH", 1f);
        if (strength <= 0f) return;

        bool det = ctx.DeterministicCapture;
        bool useSsao = ctx.Doors.Ssao && ctx.PostFX.SSAOEnabled;
        var gbuffer = ctx.GBuffer;

        denoiseCb.Write(new DenoiseConstants
        {
            W = (uint)indirect.Width, H = (uint)indirect.Height,
            UseSsao = useSsao ? 1f : 0f,
            FrameIndex = det ? -1f : (frameCounter & 1023),
            Strength = strength,
        });

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        // table order: t0 Indirect SRV, t1 depth SRV, t2 normal SRV, t3 SSAO SRV, u0 Filtered UAV.
        // COMPUTE read → the SRV must be in NON_PIXEL_SHADER_RESOURCE, not the pixel-only state (a compute SRV
        // read of a pixel-state resource is a GPU hazard / debug-layer error — the same heap/state class as the
        // known bindless-hang gotcha). `indirect` arrives in UnorderedAccess from Sample; move it to non-pixel.
        indirect.ColorToNonPixelShaderResource();
        dev.Device.CopyDescriptorsSimple(1, denoiseSrv.Cpu(0), indirect.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, denoiseSrv.Cpu(1), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, denoiseSrv.Cpu(2), gbuffer.ColorSrvCpu(1), heapType);     // G1 normal
        // SSAO: bind the real AO target when it ran this frame, else bind the normal SRV as a harmless stand-in
        // (UseSsao=0 makes the shader ignore it). AoResult is a valid SRV handle either way. The GTAO target is
        // left in the pixel-only state for the deferred pixel read, so a COMPUTE read here needs it moved to
        // NON_PIXEL_SHADER_RESOURCE first (same heap/state hazard class as Finding 1).
        if (useSsao) ctx.AoToNonPixelShaderResource?.Invoke();
        dev.Device.CopyDescriptorsSimple(1, denoiseSrv.Cpu(3),
            useSsao ? ctx.AoResult : gbuffer.ColorSrvCpu(1), heapType);
        dev.Device.CreateUnorderedAccessView(indirectFiltered.RenderTarget, null,
            new UnorderedAccessViewDescription { ViewDimension = UnorderedAccessViewDimension.Texture2D, Format = Dx12OffscreenTarget.HdrFormat },
            denoiseSrv.Cpu(4));

        gbuffer.DepthToNonPixelShaderResource();
        indirectFiltered.ColorToUnorderedAccess();

        dev.ExecuteSync(cl =>
        {
            cl.SetDescriptorHeaps(denoiseSrv.Heap);
            cl.SetComputeRootSignature(denoiseRootSig);
            cl.SetPipelineState(denoisePso);
            cl.SetComputeRootConstantBufferView(0, denoiseCb.Gpu);
            cl.SetComputeRootDescriptorTable(1, denoiseSrv.Gpu(0));
            cl.Dispatch((uint)((indirect.Width + 7) / 8), (uint)((indirect.Height + 7) / 8), 1);
        });
        denoisedThisFrame = true;
    }

    // ---- Pass 3: combine (additive) ----
    unsafe void Combine(Dx12FrameContext ctx)
    {
        var target = ctx.SceneColor;
        bool debug = ctx.PostFX.DdgiDebugRawIndirect || Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_DEBUG") == "1";

        // AO bite on the GI indirect — honour the user's DdgiAoStrength VERBATIM (do NOT force it up). Biting the
        // GI indirect with AoResult is wrong here: in a sealed box every point reads ~0 sky-visibility, so a full
        // bite crushes the indirect to black (the sphere's underside / the closed corners go pure black). AO belongs
        // on the SKY/IBL ambient (deferred already applies it there), NOT on the probe bounce. Default DdgiAoStrength
        // is 0 → indirect untouched; a user who wants probe-contact darkening dials it up explicitly.
        combineCb.Write(new CombineConstants
        {
            AoStrength = ctx.PostFX.DdgiAoStrength,
            Intensity = 1f,
            UseNearField = nearFieldThisFrame ? 1f : 0f,
            // Near-field blend strength: how strongly the SSGI contact GI is added on top of the DDGI far-field
            // (weighted per-pixel by the near-field coverage in nearField.a). 1 = full.
            NearFieldBlend = nearFieldThisFrame ? EnvF("BALLISTIC_DX12_DDGI_NEARFIELD_BLEND", 1f) : 0f,
        });

        // A3: read the denoised indirect when the spatial filter ran this frame; otherwise the raw Sample output.
        var src = denoisedThisFrame ? indirectFiltered : indirect;
        src.ColorToShaderResource();

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        dev.Device.CopyDescriptorsSimple(1, combineSrv.Cpu(0), src.ColorSrvCpu, heapType);     // t0 finished indirect (E*albedo/π)
        // t1 near-field SSGI contribution. Bind the real target when it ran, else the indirect SRV as a harmless
        // stand-in (UseNearField=0 makes the shader ignore it). Near-field is in UAV state from its dispatch.
        if (nearFieldThisFrame) {
            nearField.ColorToShaderResource();
            dev.Device.CopyDescriptorsSimple(1, combineSrv.Cpu(1), nearField.ColorSrvCpu, heapType);
        } else {
            dev.Device.CopyDescriptorsSimple(1, combineSrv.Cpu(1), src.ColorSrvCpu, heapType);
        }

        target.RenderColorOnly(cl =>
        {
            cl.SetDescriptorHeaps(combineSrv.Heap);
            cl.SetGraphicsRootSignature(combineRootSig);
            cl.SetPipelineState(debug ? combineDebugPso : combinePso);
            cl.SetGraphicsRootConstantBufferView(0, combineCb.Gpu);
            cl.SetGraphicsRootDescriptorTable(1, combineSrv.Gpu(0));
            cl.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
    }

    unsafe void BuildPipelines()
    {
        BuildRelightPipeline();
        BuildSamplePipeline();
        BuildNearFieldPipeline();
        BuildDenoisePipeline();
        BuildCombinePipeline();
        BuildDebugPipeline();
    }

    unsafe void BuildRelightPipeline()
    {
        var cbv0 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var tlas = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All);   // t0
        var irradUav = new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(0, 0), ShaderVisibility.All); // u0
        var prevIrrad = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.All); // t1
        var rtInst = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(2, 0), ShaderVisibility.All);   // t2
        var mats = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(3, 0), ShaderVisibility.All);     // t3
        var lights = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(4, 0), ShaderVisibility.All);   // t4
        var skyRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 5);                    // t5 table
        var skyTable = new RootParameter1(new RootDescriptorTable1(skyRange), ShaderVisibility.All);
        var visUav = new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(1, 0), ShaderVisibility.All);  // u1 Visibility
        var prevVis = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(6, 0), ShaderVisibility.All);  // t6 PrevVis
        var probeStateP = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(7, 0), ShaderVisibility.All); // t7 ProbeState
        var clamp = StaticClamp(0);
        relightRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(
                RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
                new[] { cbv0, tlas, irradUav, prevIrrad, rtInst, mats, lights, skyTable, visUav, prevVis, probeStateP }, new[] { clamp })));

        string hlsl = EmbeddedShaderSource.ReadHlsl("DdgiRelight.hlsl");
        byte[] cs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSMain", "DdgiRelight.hlsl");
        relightPso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription { RootSignature = relightRootSig, ComputeShader = cs });
        relightCb = new Dx12FrameCb<RelightConstants>(dev);
    }

    unsafe void BuildSamplePipeline()
    {
        var cbv0 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var irrad = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(2, 0), ShaderVisibility.All);   // t2 Irradiance (root SRV)
        var visMom = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(3, 0), ShaderVisibility.All);  // t3 VisMoments (root SRV)
        var probeStateP = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(4, 0), ShaderVisibility.All); // t4 ProbeState (root SRV)
        // table (heap slots in order): t0 depth, t1 normal (SRV), u0 Indirect (UAV), t5 albedo (SRV). Albedo is
        // folded into the indirect HERE (compute) so the combine PS never touches the G-buffer.
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 2, baseShaderRegister: 0,
            registerSpace: 0, offsetInDescriptorsFromTableStart: 0);
        var uavRange = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 0,
            registerSpace: 0, offsetInDescriptorsFromTableStart: 2);
        var albedoRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 5,
            registerSpace: 0, offsetInDescriptorsFromTableStart: 3);
        var table = new RootParameter1(new RootDescriptorTable1(srvRange, uavRange, albedoRange), ShaderVisibility.All);
        var clamp = StaticClamp(0);
        sampleRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv0, irrad, visMom, probeStateP, table }, new[] { clamp })));

        string hlsl = EmbeddedShaderSource.ReadHlsl("DdgiSample.hlsl");
        byte[] cs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSMain", "DdgiSample.hlsl");
        samplePso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription { RootSignature = sampleRootSig, ComputeShader = cs });
        sampleCb = new Dx12FrameCb<SampleConstants>(dev);
        sampleSrv = new Dx12DescriptorHeap(dev, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            4, shaderVisible: true, framesInFlight: dev.FramesInFlight);
    }

    unsafe void BuildNearFieldPipeline()
    {
        var cbv0 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        // One table: t0 depth, t1 normal, t2 SceneColor, t3 albedo (4 SRVs), then u0 NearField (UAV at offset 4).
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 4, baseShaderRegister: 0,
            registerSpace: 0, offsetInDescriptorsFromTableStart: 0);
        var uavRange = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 0,
            registerSpace: 0, offsetInDescriptorsFromTableStart: 4);
        var table = new RootParameter1(new RootDescriptorTable1(srvRange, uavRange), ShaderVisibility.All);
        var clamp = StaticClamp(0);
        nearFieldRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv0, table }, new[] { clamp })));

        string hlsl = EmbeddedShaderSource.ReadHlsl("DdgiNearField.hlsl");
        byte[] cs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSMain", "DdgiNearField.hlsl");
        nearFieldPso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription { RootSignature = nearFieldRootSig, ComputeShader = cs });
        nearFieldCb = new Dx12FrameCb<NearFieldConstants>(dev);
        nearFieldSrv = new Dx12DescriptorHeap(dev, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            5, shaderVisible: true, framesInFlight: dev.FramesInFlight);
    }

    unsafe void BuildDenoisePipeline()
    {
        var cbv0 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        // One table: t0 Indirect, t1 depth, t2 normal, t3 SSAO (4 contiguous SRVs), then u0 Filtered (UAV at offset 4).
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 4, baseShaderRegister: 0,
            registerSpace: 0, offsetInDescriptorsFromTableStart: 0);
        var uavRange = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 0,
            registerSpace: 0, offsetInDescriptorsFromTableStart: 4);
        var table = new RootParameter1(new RootDescriptorTable1(srvRange, uavRange), ShaderVisibility.All);
        var clamp = StaticClamp(0);
        denoiseRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv0, table }, new[] { clamp })));

        string hlsl = EmbeddedShaderSource.ReadHlsl("DdgiSpatialDenoise.hlsl");
        byte[] cs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSMain", "DdgiSpatialDenoise.hlsl");
        denoisePso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription { RootSignature = denoiseRootSig, ComputeShader = cs });
        denoiseCb = new Dx12FrameCb<DenoiseConstants>(dev);
        denoiseSrv = new Dx12DescriptorHeap(dev, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            5, shaderVisible: true, framesInFlight: dev.FramesInFlight);
    }

    unsafe void BuildCombinePipeline()
    {
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.Pixel);
        // DataVolatile (not the default DataStaticWhileSetAtExecute) — SAME fix as the SSR combine (Dx12ReflectionsPass
        // BuildSsr). t0 = `indirect` is a transient/aliasable target, so the DATA_STATIC "state won't change after
        // SetDescriptorTable" promise is false → GBV raised InvalidSubresourceState "(assumed at first use)" on the
        // bind (the G-buffer albedo at t1 reported as RENDER_TARGET) → the PS read zero albedo → E*albedo=0 → DDGI
        // added NOTHING (GI on/off byte-identical). DataVolatile only RELAXES a driver caching assumption (pixel-
        // neutral) and is the spec-correct flag for an aliasable resource; harmless for the committed G-buffer SRVs.
        // t0 Indirect (DDGI far), t1 NearField (A4 SSGI). Both transient/aliasable → DataVolatile.
        var range = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 2, baseShaderRegister: 0,
            registerSpace: 0, offsetInDescriptorsFromTableStart: 0, flags: DescriptorRangeFlags.DataVolatile);
        var table = new RootParameter1(new RootDescriptorTable1(range), ShaderVisibility.Pixel);
        var clamp = StaticClamp(0, ShaderVisibility.Pixel);
        combineRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, table }, new[] { clamp })));

        string hlsl = EmbeddedShaderSource.ReadHlsl("DdgiCombine.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSCombine", "DdgiCombine.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSCombine", "DdgiCombine.hlsl");
        byte[] psDebug = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSDebugE", "DdgiCombine.hlsl");
        var additive = new BlendDescription(Blend.One, Blend.One);
        GraphicsPipelineStateDescription Make(byte[] pixel, BlendDescription blend) => new()
        {
            RootSignature = combineRootSig, VertexShader = vs, PixelShader = pixel, InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = blend,
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat },
            DepthStencilFormat = Format.Unknown, SampleDescription = new SampleDescription(1, 0),
        };
        combinePso = dev.Device.CreateGraphicsPipelineState(Make(ps, additive));
        combineDebugPso = dev.Device.CreateGraphicsPipelineState(Make(psDebug, BlendDescription.Opaque));
        combineCb = new Dx12FrameCb<CombineConstants>(dev);
        combineSrv = new Dx12DescriptorHeap(dev, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            2, shaderVisible: true, framesInFlight: dev.FramesInFlight);
    }

    unsafe void BuildDebugPipeline()
    {
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var irrad = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All);  // t0 Irradiance (root SRV)
        debugRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, irrad }, System.Array.Empty<StaticSamplerDescription>())));

        string hlsl = EmbeddedShaderSource.ReadHlsl("DdgiDebugProbes.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "DdgiDebugProbes.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "DdgiDebugProbes.hlsl");
        // Depth-tested (LessEqual, no write) against the scene depth so probes behind geometry are hidden; OPAQUE.
        var ds = DepthStencilDescription.Default;
        ds.DepthWriteMask = DepthWriteMask.Zero;
        ds.DepthFunc = ComparisonFunction.LessEqual;
        debugPso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = debugRootSig, VertexShader = vs, PixelShader = ps, InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
            DepthStencilState = ds,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat },
            DepthStencilFormat = Dx12GBuffer.DepthFormat, SampleDescription = new SampleDescription(1, 0),
        });
        debugCb = new Dx12FrameCb<DebugConstants>(dev);
    }

    static StaticSamplerDescription StaticClamp(int reg, ShaderVisibility vis = ShaderVisibility.All) => new(vis, (uint)reg, 0u)
    {
        Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
        AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1, ComparisonFunction = ComparisonFunction.Never,
        MinLOD = 0, MaxLOD = float.MaxValue,
    };

    public void Dispose()
    {
        grid.Dispose();
        placementQuery?.Dispose();
        indirect?.Dispose(); indirectFiltered?.Dispose(); nearField?.Dispose();
        relightCb?.Dispose(); sampleCb?.Dispose(); nearFieldCb?.Dispose(); denoiseCb?.Dispose(); combineCb?.Dispose(); debugCb?.Dispose();
        sampleSrv?.Dispose(); nearFieldSrv?.Dispose(); denoiseSrv?.Dispose(); combineSrv?.Dispose();
        relightRootSig?.Dispose(); sampleRootSig?.Dispose(); nearFieldRootSig?.Dispose(); denoiseRootSig?.Dispose(); combineRootSig?.Dispose(); debugRootSig?.Dispose();
        relightPso?.Dispose(); samplePso?.Dispose(); nearFieldPso?.Dispose(); denoisePso?.Dispose(); combinePso?.Dispose(); combineDebugPso?.Dispose(); debugPso?.Dispose();
    }
}
