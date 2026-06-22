using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

public sealed class Dx12DdgiPass : IRenderPass, IDisposable
{
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.GlobalIllumination;
    public string Name => "DDGI";

    readonly Dx12Device dev;
    readonly Dx12DdgiProbeGrid grid;
    public Dx12DdgiProbeGrid Grid => grid;

    GpuSceneQuery placementQuery;
    bool placementPending;
    Dx12SceneAS lastSceneAS;
    Dx12EmissiveLights emissiveLights;

    ID3D12RootSignature relightRootSig;
    ID3D12PipelineState relightPso;
    Dx12FrameCb<RelightConstants> relightCb;
    const int RelightSkyTableBase = Dx12BindlessTail.DdgiRelightTableBase;
    const int RelightRays = 64;

    ID3D12RootSignature sampleRootSig;
    ID3D12PipelineState samplePso;
    Dx12FrameCb<SampleConstants> sampleCb;
    Dx12DescriptorHeap sampleSrv;
    Dx12OffscreenTarget indirect;

    ID3D12RootSignature denoiseRootSig;
    ID3D12PipelineState denoisePso;
    Dx12FrameCb<DenoiseConstants> denoiseCb;
    Dx12DescriptorHeap denoiseSrv;
    Dx12OffscreenTarget indirectFiltered;
    bool denoisedThisFrame;

    ID3D12RootSignature nearFieldRootSig;
    ID3D12PipelineState nearFieldPso;
    Dx12FrameCb<NearFieldConstants> nearFieldCb;
    Dx12DescriptorHeap nearFieldSrv;
    Dx12OffscreenTarget nearField;
    bool nearFieldThisFrame;

    ID3D12RootSignature combineRootSig;
    ID3D12PipelineState combinePso, combineDebugPso;
    Dx12FrameCb<CombineConstants> combineCb;
    Dx12DescriptorHeap combineSrv;

    [StructLayout(LayoutKind.Sequential)]
    struct RelightConstants
    {
        public Vector3 GridOrigin;   public float RayCount;
        public Vector3 ProbeSpacing; public float SkyIntensity;
        public uint CountX, CountY, CountZ; public float UseSky;
        public Vector3 SunDir;       public float SunBias;
        public Vector3 SunColor;     public float LightCount;
        public float EmaAlpha;       public float HistoryValid; public float Intensity; public float FrameJitter;
        public float MultiBounce;    public float BounceBoost;  public float UsePlacement; public float ValidateOn;
        public float EmissiveCount;  public float NeePad0, NeePad1, NeePad2;
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
        public uint W, H; public float UseSsao; public float FrameIndex;
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
        if (!run) grid.ResetHistory();
        return run;
    }

    static bool placementEnabled = Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_NOPLACEMENT") != "1";

    int gridX, gridY, gridZ;
    bool useVolumeBounds;
    Vector3 boundsMin, boundsMax;

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

        Vector3 e = ctx.PostFX.DdgiBoundsExtent;
        Vector3 c = ctx.PostFX.DdgiBoundsCenter;
        useVolumeBounds = ctx.PostFX.DdgiBoundsMode == 1 && e.X > 1e-3f && e.Y > 1e-3f && e.Z > 1e-3f
                          && Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_BOUNDS") != "0";
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
    Vector3 prevSunDir = new(float.NaN, 0, 0);
    Vector3 prevSunColor;

    public void Resize(int width, int height)
    {
        indirect?.Dispose();
        indirect = new Dx12OffscreenTarget(dev, width, height, colorFormat: Dx12OffscreenTarget.HdrFormat,
            colorReadable: true, allowUav: true);
        indirectFiltered?.Dispose();
        indirectFiltered = new Dx12OffscreenTarget(dev, width, height, colorFormat: Dx12OffscreenTarget.HdrFormat,
            colorReadable: true, allowUav: true);
        nearField?.Dispose();
        nearField = new Dx12OffscreenTarget(dev, width, height, colorFormat: Dx12OffscreenTarget.HdrFormat,
            colorReadable: true, allowUav: true);
    }

    public unsafe void Record(Dx12FrameContext ctx)
    {
        ReadGrid(ctx);
        frameCounter++;

        var sceneAS = ctx.Dxr.SceneAS;
        sceneAS.Ensure(ctx.WholeMeshRenderers);

        if (!grid.Ensure(ctx, gridX, gridY, gridZ, useVolumeBounds, boundsMin, boundsMax)) return;

        var rtGeo = ctx.Dxr.RtGeometry;
        ctx.GpuDriven.EnsureMaterialTable(ctx.WholeMeshRenderers);
        rtGeo.Ensure(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection, ctx.GpuDriven);
        if (!rtGeo.Valid) return;

        if (!logged) { logged = true; Console.WriteLine($"    DDGI [GlobalIllumination=500] {grid.CountX}x{grid.CountY}x{grid.CountZ}={grid.ProbeCount} probes"); }

        if (placementEnabled && !grid.StatePlaced) { placementPending = true; lastSceneAS = sceneAS; }

        bool neeOn = Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_EMISSIVE_NEE") != "0";
        if (neeOn) { emissiveLights ??= new Dx12EmissiveLights(dev); emissiveLights.Ensure(RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection); }

        Relight(ctx, sceneAS, rtGeo);
        Sample(ctx);
        NearField(ctx);
        Denoise(ctx);
        Combine(ctx);
        string probesEnv = Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_DEBUG_PROBES");
        if (probesEnv != "0" && (ctx.PostFX.DdgiDebugProbes || probesEnv == "1"))
            DrawProbes(ctx);
    }

    bool logged;

    public void RunPendingPlacement()
    {
        if (!placementPending || grid.StatePlaced || lastSceneAS == null) return;
        if (!lastSceneAS.Valid) return;
        placementQuery ??= new GpuSceneQuery(dev, lastSceneAS, trustSharedScene: true);
        grid.PlaceProbes(dev, placementQuery);
        placementPending = false;
    }

    unsafe void DrawProbes(Dx12FrameContext ctx)
    {
        var target = ctx.SceneColor;
        var gbuffer = ctx.GBuffer;

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

        var irrad = grid.IrradianceRead;
        gbuffer.DepthToReadOnly();

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

    unsafe void Relight(Dx12FrameContext ctx, Dx12SceneAS sceneAS, Dx12RtGeometry rtGeo)
    {
        Vector3 sunDir = ctx.LightDir.LengthSquared() < 1e-8f ? Vector3.UnitY : Vector3.Normalize(ctx.LightDir);
        bool useSky = ctx.Ibl != null && ctx.Ibl.HasBaked;
        float intensity = EnvF("BALLISTIC_DX12_DDGI_INTENSITY", ctx.PostFX.DdgiIntensity);
        float ema = EnvF("BALLISTIC_DX12_DDGI_ALPHA", ctx.PostFX.DdgiEmaAlpha);
        bool det = ctx.DeterministicCapture;

        bool lightChanged = !det && (Vector3.DistanceSquared(prevSunDir, sunDir) > 1e-6f
                                     || Vector3.DistanceSquared(prevSunColor, ctx.LightColor) > 1e-4f);
        prevSunDir = sunDir; prevSunColor = ctx.LightColor;
        if (lightChanged) ema = MathF.Max(ema, 0.5f);

        float frameJitter = det ? -1f : (frameCounter & 1023);
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
            ValidateOn = Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_VALIDATE") == "0" ? 0f : 1f,
            EmissiveCount = (emissiveLights is { Valid: true }) ? emissiveLights.Count : 0f,
            NeePad0 = EnvF("BALLISTIC_DX12_DDGI_NEE_INTENSITY", 1f),
        });

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Dx12DescriptorHeap bindless = Dx12Backend.BindlessHeap;
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
            cl.SetComputeRootShaderResourceView(1, sceneAS.TlasAddress);
            cl.SetComputeRootUnorderedAccessView(2, grid.IrradianceWriteGpu);
            cl.SetComputeRootShaderResourceView(3, grid.IrradianceReadGpu);
            cl.SetComputeRootShaderResourceView(4, rtGeo.InstancesGpuAddress);
            cl.SetComputeRootShaderResourceView(5, ctx.GpuDriven.MaterialsGpuAddress);
            cl.SetComputeRootShaderResourceView(6, ctx.ClusteredLights.LightBufGpuAddress);
            cl.SetComputeRootDescriptorTable(7, bindless.Gpu(RelightSkyTableBase));
            cl.SetComputeRootUnorderedAccessView(8, grid.VisibilityWriteGpu);
            cl.SetComputeRootShaderResourceView(9, grid.VisibilityReadGpu);
            cl.SetComputeRootShaderResourceView(10, grid.ProbeStateGpu);
            ulong neeAddr = (emissiveLights is { Valid: true }) ? emissiveLights.GpuAddress
                                                               : ctx.ClusteredLights.LightBufGpuAddress;
            cl.SetComputeRootShaderResourceView(11, neeAddr);
            cl.Dispatch((uint)grid.ProbeCount, 1, 1);
            cl.ResourceBarrierTransition(irradW, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
            cl.ResourceBarrierTransition(visW, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
        });
        grid.SetState(irradW, ResourceStates.NonPixelShaderResource);
        grid.SetState(visW, ResourceStates.NonPixelShaderResource);
        grid.SwapAndMarkHistory();
    }

    unsafe void Sample(Dx12FrameContext ctx)
    {
        var gbuffer = ctx.GBuffer;
        Matrix4x4.Invert(ctx.ViewProj, out Matrix4x4 invVP);
        var irrad = grid.IrradianceRead;

        sampleCb.Write(new SampleConstants
        {
            InvViewProj = Matrix4x4.Transpose(invVP),
            GridOrigin = grid.GridOrigin, ProbeSpacing = grid.ProbeSpacing,
            NormalBias = EnvF("BALLISTIC_DX12_DDGI_NORMALBIAS", ctx.PostFX.DdgiNormalBias),
            CountX = (uint)grid.CountX, CountY = (uint)grid.CountY, CountZ = (uint)grid.CountZ,
            W = (uint)indirect.Width, H = (uint)indirect.Height,
            Intensity = EnvF("BALLISTIC_DX12_DDGI_INTENSITY", ctx.PostFX.DdgiIntensity),
            UseVisibility = (Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_NOVIS") == "1" || !ctx.PostFX.DdgiVisibility) ? 0f : 1f,
            UsePlacement = (placementEnabled && grid.StatePlaced) ? 1f : 0f,
        });

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        dev.Device.CopyDescriptorsSimple(1, sampleSrv.Cpu(0), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, sampleSrv.Cpu(1), gbuffer.ColorSrvCpu(1), heapType);
        dev.Device.CreateUnorderedAccessView(indirect.RenderTarget, null,
            new UnorderedAccessViewDescription { ViewDimension = UnorderedAccessViewDimension.Texture2D, Format = Dx12OffscreenTarget.HdrFormat },
            sampleSrv.Cpu(2));
        dev.Device.CopyDescriptorsSimple(1, sampleSrv.Cpu(3), gbuffer.ColorSrvCpu(0), heapType);

        gbuffer.DepthToNonPixelShaderResource();
        indirect.ColorToUnorderedAccess();

        dev.ExecuteSync(cl =>
        {
            cl.SetDescriptorHeaps(sampleSrv.Heap);
            cl.SetComputeRootSignature(sampleRootSig);
            cl.SetPipelineState(samplePso);
            cl.SetComputeRootConstantBufferView(0, sampleCb.Gpu);
            cl.SetComputeRootShaderResourceView(1, irrad.GPUVirtualAddress);
            cl.SetComputeRootShaderResourceView(2, grid.VisibilityRead.GPUVirtualAddress);
            cl.SetComputeRootShaderResourceView(3, grid.ProbeStateGpu);
            cl.SetComputeRootDescriptorTable(4, sampleSrv.Gpu(0));
            cl.Dispatch((uint)((indirect.Width + 7) / 8), (uint)((indirect.Height + 7) / 8), 1);
        });
    }

    unsafe void NearField(Dx12FrameContext ctx)
    {
        nearFieldThisFrame = false;
        string env = Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_NEARFIELD");
        if (env == "0") return;
        float intensity = EnvF("BALLISTIC_DX12_DDGI_NEARFIELD_INTENSITY", 1f);
        if (intensity <= 0f) return;

        bool det = ctx.DeterministicCapture;
        var gbuffer = ctx.GBuffer;
        var scene = ctx.Target;
        Matrix4x4.Invert(ctx.Proj, out Matrix4x4 invProj);

        nearFieldCb.Write(new NearFieldConstants
        {
            InvProjection = Matrix4x4.Transpose(invProj),
            Projection = Matrix4x4.Transpose(ctx.Proj),
            View = Matrix4x4.Transpose(ctx.View),
            W = (uint)nearField.Width, H = (uint)nearField.Height,
            Radius = EnvF("BALLISTIC_DX12_DDGI_NEARFIELD_RADIUS", 1.5f),
            FrameIndex = det ? -1f : (frameCounter & 1023),
            SliceCount = 3f, StepCount = 8f,
            Intensity = intensity, Thickness = 0.5f,
        });

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        gbuffer.DepthToNonPixelShaderResource();
        scene.ColorToNonPixelShaderResource();
        nearField.ColorToUnorderedAccess();
        dev.Device.CopyDescriptorsSimple(1, nearFieldSrv.Cpu(0), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, nearFieldSrv.Cpu(1), gbuffer.ColorSrvCpu(1), heapType);
        dev.Device.CopyDescriptorsSimple(1, nearFieldSrv.Cpu(2), scene.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, nearFieldSrv.Cpu(3), gbuffer.ColorSrvCpu(0), heapType);
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
        scene.ColorToRenderTarget();
        nearFieldThisFrame = true;
    }

    unsafe void Denoise(Dx12FrameContext ctx)
    {
        denoisedThisFrame = false;
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
        indirect.ColorToNonPixelShaderResource();
        dev.Device.CopyDescriptorsSimple(1, denoiseSrv.Cpu(0), indirect.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, denoiseSrv.Cpu(1), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, denoiseSrv.Cpu(2), gbuffer.ColorSrvCpu(1), heapType);
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

    unsafe void Combine(Dx12FrameContext ctx)
    {
        var target = ctx.SceneColor;
        bool debug = ctx.PostFX.DdgiDebugRawIndirect || Environment.GetEnvironmentVariable("BALLISTIC_DX12_DDGI_DEBUG") == "1";

        combineCb.Write(new CombineConstants
        {
            AoStrength = ctx.PostFX.DdgiAoStrength,
            Intensity = 1f,
            UseNearField = nearFieldThisFrame ? 1f : 0f,
            NearFieldBlend = nearFieldThisFrame ? EnvF("BALLISTIC_DX12_DDGI_NEARFIELD_BLEND", 1f) : 0f,
        });

        var src = denoisedThisFrame ? indirectFiltered : indirect;
        src.ColorToShaderResource();

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        dev.Device.CopyDescriptorsSimple(1, combineSrv.Cpu(0), src.ColorSrvCpu, heapType);
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
        var tlas = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var irradUav = new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var prevIrrad = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.All);
        var rtInst = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(2, 0), ShaderVisibility.All);
        var mats = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(3, 0), ShaderVisibility.All);
        var lights = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(4, 0), ShaderVisibility.All);
        var skyRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 5);
        var skyTable = new RootParameter1(new RootDescriptorTable1(skyRange), ShaderVisibility.All);
        var visUav = new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(1, 0), ShaderVisibility.All);
        var prevVis = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(6, 0), ShaderVisibility.All);
        var probeStateP = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(7, 0), ShaderVisibility.All);
        var emissiveP = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(8, 0), ShaderVisibility.All);
        var clamp = StaticClamp(0);
        relightRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(
                RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
                new[] { cbv0, tlas, irradUav, prevIrrad, rtInst, mats, lights, skyTable, visUav, prevVis, probeStateP, emissiveP }, new[] { clamp })));

        string hlsl = EmbeddedShaderSource.ReadHlsl("DdgiRelight.hlsl");
        byte[] cs = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSMain", "DdgiRelight.hlsl");
        relightPso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription { RootSignature = relightRootSig, ComputeShader = cs });
        relightCb = new Dx12FrameCb<RelightConstants>(dev);
    }

    unsafe void BuildSamplePipeline()
    {
        var cbv0 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var irrad = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(2, 0), ShaderVisibility.All);
        var visMom = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(3, 0), ShaderVisibility.All);
        var probeStateP = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(4, 0), ShaderVisibility.All);
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
        var irrad = new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        debugRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, irrad }, System.Array.Empty<StaticSamplerDescription>())));

        string hlsl = EmbeddedShaderSource.ReadHlsl("DdgiDebugProbes.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "DdgiDebugProbes.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "DdgiDebugProbes.hlsl");
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
        emissiveLights?.Dispose();
        indirect?.Dispose(); indirectFiltered?.Dispose(); nearField?.Dispose();
        relightCb?.Dispose(); sampleCb?.Dispose(); nearFieldCb?.Dispose(); denoiseCb?.Dispose(); combineCb?.Dispose(); debugCb?.Dispose();
        sampleSrv?.Dispose(); nearFieldSrv?.Dispose(); denoiseSrv?.Dispose(); combineSrv?.Dispose();
        relightRootSig?.Dispose(); sampleRootSig?.Dispose(); nearFieldRootSig?.Dispose(); denoiseRootSig?.Dispose(); combineRootSig?.Dispose(); debugRootSig?.Dispose();
        relightPso?.Dispose(); samplePso?.Dispose(); nearFieldPso?.Dispose(); denoisePso?.Dispose(); combinePso?.Dispose(); combineDebugPso?.Dispose(); debugPso?.Dispose();
    }
}
