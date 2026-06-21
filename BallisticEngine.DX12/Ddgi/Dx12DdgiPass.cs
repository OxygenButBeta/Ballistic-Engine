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
        public float MultiBounce;    public float BounceBoost;  public float Pad0;      public float Pad1;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SampleConstants
    {
        public Matrix4x4 InvViewProj;
        public Vector3 GridOrigin;   public float Pad0;
        public Vector3 ProbeSpacing; public float NormalBias;
        public uint CountX, CountY, CountZ; public uint W;
        public uint H; public float Intensity; public float UseVisibility; public float Pad2;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CombineConstants { public float AoStrength; public float Intensity; public float Pad0; public float Pad1; }

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

    int gridX, gridY, gridZ;
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
    }

    public unsafe void Record(Dx12FrameContext ctx)
    {
        ReadGrid(ctx);
        frameCounter++;

        // Build/refresh the shared TLAS (DDGI may be the first RT effect in the frame — RT shadows/reflections
        // can be off). Stamp-cached: a static scene builds once. Without this the AS is never Valid → no-op.
        var sceneAS = ctx.Dxr.SceneAS;
        sceneAS.Ensure(ctx.WholeMeshRenderers);

        if (!grid.Ensure(ctx, gridX, gridY, gridZ)) return;

        var rtGeo = ctx.Dxr.RtGeometry;
        // Ensure the bindless material table + per-instance geo SRVs are fresh (stamp-cached no-ops if a prior
        // RT pass already built them this frame).
        ctx.GpuDriven.EnsureMaterialTable(ctx.WholeMeshRenderers);
        rtGeo.Ensure(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection, ctx.GpuDriven);
        if (!rtGeo.Valid) return;

        if (!logged) { logged = true; Console.WriteLine($"    DDGI [GlobalIllumination=500] {grid.CountX}x{grid.CountY}x{grid.CountZ}={grid.ProbeCount} probes"); }

        Relight(ctx, sceneAS, rtGeo);
        Sample(ctx);
        Combine(ctx);
        // Probe-sphere debug overlay: GiVolume.debugProbes toggle OR the env door.
        if (ctx.PostFX.DdgiDebugProbes || Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_DEBUG_PROBES") == "1")
            DrawProbes(ctx);
    }

    bool logged;

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
        bool useSky = ctx.Ibl != null;
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

        relightCb.Write(new RelightConstants
        {
            GridOrigin = grid.GridOrigin, RayCount = RelightRays,
            ProbeSpacing = grid.ProbeSpacing, SkyIntensity = EnvF("BALLISTIC_DX12_DDGI_SKY", ctx.PostFX.DdgiSkyIntensity),
            CountX = (uint)grid.CountX, CountY = (uint)grid.CountY, CountZ = (uint)grid.CountZ,
            UseSky = useSky ? 1f : 0f,
            SunDir = sunDir, SunBias = 0.05f,
            SunColor = ctx.LightColor, LightCount = ctx.ClusteredLights.LightCount,
            EmaAlpha = ema, HistoryValid = (grid.HistoryValid && !det) ? 1f : 0f,
            // FrameJitter = -1 → the relight uses a FIXED per-probe ray rotation every frame. A rotating jitter
            // (frameCounter%64) re-aimed all 64 rays every frame, so on a STATIC scene each probe's integral kept
            // jumping to a different sparse estimate and the EMA never settled → the visible flicker (probe colors
            // changing while nothing moves). With a fixed ray set the integral is identical every frame, so the EMA
            // converges and holds. Deterministic capture already used -1; now the live path does too.
            Intensity = intensity, FrameJitter = -1f,
            MultiBounce = Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_NOBOUNCE") == "1" ? 0f
                          : (ctx.PostFX.DdgiMultiBounce ? 1f : 0f),
            BounceBoost = EnvF("BALLISTIC_DX12_DDGI_BOUNCE_BOOST", 1f),
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
            NormalBias = EnvF("BALLISTIC_DX12_DDGI_NORMALBIAS", 0.2f),
            CountX = (uint)grid.CountX, CountY = (uint)grid.CountY, CountZ = (uint)grid.CountZ,
            W = (uint)indirect.Width, H = (uint)indirect.Height,
            Intensity = 1f,
            UseVisibility = (Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_NOVIS") == "1" || !ctx.PostFX.DdgiVisibility) ? 0f : 1f,
        });

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        // table: depth SRV (t0), normal SRV (t1), Indirect UAV (u0)
        dev.Device.CopyDescriptorsSimple(1, sampleSrv.Cpu(0), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, sampleSrv.Cpu(1), gbuffer.ColorSrvCpu(1), heapType);
        // Indirect UAV — create into slot 2.
        dev.Device.CreateUnorderedAccessView(indirect.RenderTarget, null,
            new UnorderedAccessViewDescription { ViewDimension = UnorderedAccessViewDimension.Texture2D, Format = Dx12OffscreenTarget.HdrFormat },
            sampleSrv.Cpu(2));

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
            cl.SetComputeRootDescriptorTable(3, sampleSrv.Gpu(0));                        // t0 depth, t1 normal, u0 Indirect
            cl.Dispatch((uint)((indirect.Width + 7) / 8), (uint)((indirect.Height + 7) / 8), 1);
        });
    }

    // ---- Pass 3: combine (additive) ----
    unsafe void Combine(Dx12FrameContext ctx)
    {
        var target = ctx.SceneColor;
        bool debug = ctx.PostFX.DdgiDebugRawIndirect || Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_DEBUG") == "1";

        combineCb.Write(new CombineConstants
        {
            AoStrength = ctx.PostFX.DdgiAoStrength,
            Intensity = 1f,
        });

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        dev.Device.CopyDescriptorsSimple(1, combineSrv.Cpu(0), indirect.ColorSrvCpu, heapType);     // t0 E
        dev.Device.CopyDescriptorsSimple(1, combineSrv.Cpu(1), ctx.GBuffer.ColorSrvCpu(0), heapType); // t1 albedo
        dev.Device.CopyDescriptorsSimple(1, combineSrv.Cpu(2), ctx.AoResult, heapType);             // t2 AO

        // Indirect → PS-readable; the G-buffer albedo (G0) is already in the combined ShaderRead state.
        indirect.ColorToShaderResource();

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
        var clamp = StaticClamp(0);
        relightRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(
                RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
                new[] { cbv0, tlas, irradUav, prevIrrad, rtInst, mats, lights, skyTable, visUav, prevVis }, new[] { clamp })));

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
        // table: t0 depth, t1 normal (SRV) + u0 Indirect (UAV)
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 2, baseShaderRegister: 0);
        var uavRange = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 0);
        var table = new RootParameter1(new RootDescriptorTable1(srvRange, uavRange), ShaderVisibility.All);
        var clamp = StaticClamp(0);
        sampleRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv0, irrad, visMom, table }, new[] { clamp })));

        string hlsl = EmbeddedShaderSource.ReadHlsl("DdgiSample.hlsl");
        byte[] cs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSMain", "DdgiSample.hlsl");
        samplePso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription { RootSignature = sampleRootSig, ComputeShader = cs });
        sampleCb = new Dx12FrameCb<SampleConstants>(dev);
        sampleSrv = new Dx12DescriptorHeap(dev, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            3, shaderVisible: true, framesInFlight: dev.FramesInFlight);
    }

    unsafe void BuildCombinePipeline()
    {
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.Pixel);
        var range = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 3, baseShaderRegister: 0);
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
            3, shaderVisible: true, framesInFlight: dev.FramesInFlight);
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
        indirect?.Dispose();
        relightCb?.Dispose(); sampleCb?.Dispose(); combineCb?.Dispose(); debugCb?.Dispose();
        sampleSrv?.Dispose(); combineSrv?.Dispose();
        relightRootSig?.Dispose(); sampleRootSig?.Dispose(); combineRootSig?.Dispose(); debugRootSig?.Dispose();
        relightPso?.Dispose(); samplePso?.Dispose(); combinePso?.Dispose(); combineDebugPso?.Dispose(); debugPso?.Dispose();
    }
}
