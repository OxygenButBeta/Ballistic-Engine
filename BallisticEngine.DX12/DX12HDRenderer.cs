using BallisticEngine.DX12;
using BallisticEngine.Rendering;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;
using GLMatrix4 = System.Numerics.Matrix4x4;
using GLVector3 = System.Numerics.Vector3;

namespace BallisticEngine;

public sealed class DX12HDRenderer : HDRenderer
{
    readonly Dx12Device dev;

    public Dx12Device Device => dev;
    Dx12OffscreenTarget target;

    Dx12OffscreenTarget ldr;

    int targetW = 1920, targetH = 1080;
    int outputW = 1920, outputH = 1080;

    Dx12FsrUpscaler fsr;
    Dx12OffscreenTarget fsrOutput;
    bool fsrActive;
    bool fsrUnavailable;
    UpscaleMode currentUpscaleMode = UpscaleMode.Off;

    Dx12DlssUpscaler dlss;
    Dx12XessUpscaler xess;
    bool dlssUnavailable;
    bool xessUnavailable;
    UpscalerKind activeUpscaler = UpscalerKind.Fsr;
    const float FovYRadians = 45f * (MathF.PI / 180f);

    ID3D12RootSignature rootSig;
    ID3D12PipelineState pso;

    Dx12GBuffer gbuffer;
    ID3D12RootSignature gbufferRootSig;
    ID3D12PipelineState gbufferPso;

    ID3D12RootSignature skinnedGbufferRootSig;
    ID3D12PipelineState skinnedGbufferPso;
    ID3D12Resource boneMatrixRing;
    unsafe byte* boneMatrixMapped;
    long boneFrameStride;
    long BoneFrameOffset => (long)dev.FrameSlot * boneFrameStride;
    int boneMatrixSlotSize;
    int boneMatrixSlotCount;
    const int MaxBonesPerDraw = 256;

    Dx12FrameCb<MotionConstants> motionCb;
    Matrix4x4 motionPrevViewProj;
    float normalLodBiasCached;
    bool motionPrevValid;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct MotionConstants
    {
        public Matrix4x4 ViewProjCur;
        public Matrix4x4 ViewProjPrev;
        public float NormalLodBias;
        public Vector3 PadMotion;
    }

    Dx12ClusteredLights clusteredLights;

    Dx12TaaPass taaPass;
    Dx12FsrPass fsrPass;
    Dx12MotionBlurPass motionBlurPass;
    Dx12DepthOfFieldPass dofPass;
    int taaFrame;
    int frameCounter;
    Vector2 currentJitter;

    Dx12DxrShared dxr;

    ID3D12RootSignature rtShadowRootSig;
    ID3D12StateObject rtShadowPso;
    ID3D12Resource rtShadowSbt;
    Dx12FrameCb<RtShadowConstants> rtShadowCb;
    Dx12OffscreenTarget rtShadowMask;
    Dx12DescriptorHeap rtShadowHeap;
    bool rtShadowBuilt;
    bool rtShadowsThisFrame;
    const int RtSbtSlot = 64;

    const int RtShadowSoftRays = 8;

    ID3D12RootSignature rtShadowDenoiseRootSig;
    ID3D12PipelineState rtShadowDenoiseHPso, rtShadowDenoiseVPso;
    ID3D12Resource rtShadowDenoiseCb;
    unsafe byte* rtShadowDenoiseCbMapped;
    Dx12OffscreenTarget rtShadowDenoiseScratch;
    Dx12DescriptorHeap rtShadowDenoiseHeap;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct RtShadowConstants
    {
        public Matrix4x4 InvViewProj;
        public Vector3 SunDir;
        public float NormalBias;
        public float SunAngularRadius;
        public int RayCount;
        public int FrameIndex;
        public float Pad0;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct RtShadowDenoiseConstants
    {
        public Vector2 TexelSize;
        public float DepthSigma;
        public float NormalSigma;
        public Vector2 Direction;
        public Vector2 Pad;
    }

    bool? deterministicOn;

    bool DeterministicCapture =>
        deterministicOn ??= Environment.GetEnvironmentVariable("BALLISTIC_DETERMINISTIC") == "1";

    Dx12TransparentsPass transparentsPass;

    const float CameraNear = 0.1f, CameraFar = 1000f;

    Dx12CompositePass compositePass;

    Dx12GtaoPass gtaoPass;
    Dx12RtaoPass rtaoPass;
    Dx12CapsuleShadowPass capsuleShadowPass;

    Dx12DeferredLightingPass deferredPass;

    Dx12AerialPerspectivePass apPass;
    Dx12FogPass fogPass;

    Dx12SkyPass skyPass;

    Dx12DdgiPass ddgiPass;

    Dx12ReflectionsPass reflectionsPass;

    ID3D12Resource cbRing;
    int cbSlotSize;
    int cbSlotCount;
    long cbFrameStride;

    long CbFrameOffset => (long)dev.FrameSlot * cbFrameStride;
    unsafe byte* cbMapped;

    ID3D12Resource customCbRing;
    int customCbSlotSize, customCbSlotCount;
    long customCbFrameStride;
    long CustomCbFrameOffset => (long)dev.FrameSlot * customCbFrameStride;
    unsafe byte* customCbMapped;
    int customDrawSlot;

    Dx12DescriptorHeap srvVisible;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct DrawConstants
    {
        public Matrix4x4 Mvp;
        public Matrix4x4 Model;
        public Vector3 LightDir;
        public float Exposure;
        public Vector3 LightColor;
        public float Metallic;
        public Vector3 Ambient;
        public float Roughness;
        public Vector3 CameraPos;
        public float SpecularReflectance;
        public Vector4 BaseColorFactor;
        public Vector3 EmissiveFactor;
        public float HasEmissive;
        public float NormalStrength, NormalFlipY, HasMetallicMap, HasRoughnessMap;
        public float PackedOrm, Cutout, UseIBL, PrefilterMaxMip;
    }

    const int MaterialSrvCount = 6;

    const int MaxCustomTex = 4;

    Dx12IblBaker ibl;
    Dx12DescriptorHeap iblSrvVisible;
    bool iblActiveThisFrame;

    Dx12SkyLuts skyLuts;

    const int MaxCascades = 4;
    int shadowMapSize = 2048;
    int activeCascadeCount = 4;
    Dx12ShadowMap shadowMap;
    ID3D12RootSignature shadowRootSig;
    ID3D12PipelineState shadowPso;
    ID3D12Resource shadowCb;
    unsafe byte* shadowCbMapped;
    int shadowCbSlotSize, shadowCbSlotCount;
    readonly Matrix4x4[] cascadeMatrices = new Matrix4x4[MaxCascades];

    readonly System.Collections.Generic.List<(int cascade, Dx12Buffer<GLVector3> vb, Dx12IndexBuffer ib, int start,
        int count, int cbSlot)> shadowFills = new(256);
    readonly float[] cascadeDepthRanges = new float[MaxCascades];
    bool shadowsThisFrame;

    Dx12VirtualShadowMap vsm;
    bool vsmWanted;
    bool vsmActiveThisFrame;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct ShadowConstants
    {
        public Matrix4x4 LightMvp;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    unsafe struct FrameConstants
    {
        public Matrix4x4 Cascade0, Cascade1, Cascade2, Cascade3;
        public Vector4 CascadeBias;
        public float CascadeCountF;
        public float ShadowsEnabled;
        public float ShadowMapTexel;
        public float CascadeBlend;
        public float ShadowFiltering;
        public float ShadowSoftness;
        public float ContactShadowsOn;

        public float ContactShadowLength;

        public float ContactShadowSteps;
        public float ContactShadowThickness;
        public float FramePad0, FramePad1;
    }

    Dx12FrameCb<FrameConstants> frameCb;

    public DX12HDRenderer(Dx12Device device)
    {
        dev = device;
    }

    int ldrUiSlot = -1;
    nint ldrUiHandle;
    public override RenderHandle SceneColorHandle => new(ldrUiHandle);
    public override RenderHandle GameColorHandle => new(ldrUiHandle);

    public ID3D12Resource DisplayResource => ldr?.RenderTarget;
    public int DisplayWidth => outputW;
    public int DisplayHeight => outputH;

    public GpuSceneQuery CreateSceneQuery() => new GpuSceneQuery(dev);

    public override bool DisplayTextureTopDown => true;

    void RegisterLdrUi()
    {
        if (Dx12Backend.UiHeap == null) return;
        if (ldrUiSlot < 0) ldrUiSlot = Dx12Backend.UiHeap.Allocate();
        Dx12Backend.RegisterUiAt(ldrUiSlot, ldr.ColorSrvCpu);
        ldrUiHandle = (nint)Dx12Backend.UiHeap.Gpu(ldrUiSlot).Ptr;
    }

    public override void ResizeSceneTarget(int width, int height) => Resize(width, height);
    public override void ResizeGameTarget(int width, int height) => Resize(width, height);

    void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        if (target != null && width == outputW && height == outputH) return;
        outputW = width;
        outputH = height;
        fsrActive = false;
        activeUpscaler = UpscalerKind.Fsr;
        currentUpscaleMode = UpscaleMode.Off;
        fsr?.Dispose();
        fsr = null;
        DisposeVendorUpscalers();
        AllocateResolutionTargets(width, height);
    }

    void AllocateResolutionTargets(int internalW, int internalH)
    {
        dev.Flush();
        targetW = internalW;
        targetH = internalH;
        target?.Dispose();
        ldr?.Dispose();
        gbuffer?.Dispose();
        target = new Dx12OffscreenTarget(dev, internalW, internalH, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
        ldr = new Dx12OffscreenTarget(dev, outputW, outputH, colorReadable: true);
        RegisterLdrUi();
        ldr.ColorToShaderResource();
        gbuffer = new Dx12GBuffer(dev, internalW, internalH);
        motionPrevValid = false;
        if (rtShadowMask != null) AllocRtShadowMask();
        AllocFsrOutput();
        if (aliasPath && rtPool != null) RegisterAliasPool(internalW, internalH);
        graph?.Resize(internalW, internalH);
        featureBlitter?.Resize(internalW, internalH);
    }

    void RegisterAliasPool(int w, int h)
    {
        int hw = Math.Max(1, w / 2), hh = Math.Max(1, h / 2);
        var Hdr = Dx12OffscreenTarget.HdrFormat;
        int refl = graph.OrderIndexOf("Reflections");
        int gi = graph.OrderIndexOf("GI"), comp = graph.OrderIndexOf("Composite");
        rtPool.Register("ssrTarget", hw, hh, Hdr, true, refl, refl);
        rtPool.Register("ssrScene", w, h, Hdr, false, refl, refl);
        rtPool.Register("ssgiTarget", hw, hh, Hdr, true, gi, gi);
        rtPool.Register("ssgiDenoised", hw, hh, Hdr, true, gi, gi);
        rtPool.Register("ssgiScene", w, h, Hdr, false, gi, gi);
        rtPool.Register("bloomA", hw, hh, Hdr, false, comp, comp);
        rtPool.Register("bloomB", hw, hh, Hdr, false, comp, comp);
        rtPool.BuildPlan();
        string overlap = rtPool.AuditNoOverlap();
        if (overlap != null) throw new InvalidOperationException("[DX12 V2] alias plan UNSOUND — " + overlap);
    }

    void AllocFsrOutput()
    {
        fsrOutput?.Dispose();
        fsrOutput = new Dx12OffscreenTarget(dev, outputW, outputH, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: true);
    }

    static uint FsrQuality(UpscaleMode m) => m switch
    {
        UpscaleMode.NativeAA => FfxApi.QualityNativeAA,
        UpscaleMode.Quality => FfxApi.QualityQuality,
        UpscaleMode.Balanced => FfxApi.QualityBalanced,
        UpscaleMode.Performance => FfxApi.QualityPerformance,
        UpscaleMode.UltraPerformance => FfxApi.QualityUltraPerformance,
        _ => FfxApi.QualityQuality,
    };

    void EnsureUpscaleTargets(UpscaleMode mode)
    {
        UpscalerKind wantKind = UpscalerKind.Fsr;
        bool wantUpscale = mode != UpscaleMode.Off;
        if (wantUpscale)
        {
            UpscalerKind k = Dx12Upscaler.KindOf(mode);
            if (k == UpscalerKind.Dlss && !dlssUnavailable) wantKind = UpscalerKind.Dlss;
            else if (k == UpscalerKind.Xess && !xessUnavailable) wantKind = UpscalerKind.Xess;
            else wantKind = UpscalerKind.Fsr;
        }

        if (wantUpscale && wantKind == UpscalerKind.Fsr && fsrUnavailable) wantUpscale = false;

        int wantIW = outputW, wantIH = outputH;
        if (wantUpscale)
        {
            if (wantKind == UpscalerKind.Fsr)
            {
                try { (wantIW, wantIH) = Dx12FsrUpscaler.RenderResolutionFor(outputW, outputH, FsrQuality(mode)); }
                catch (Exception e)
                {
                    Console.WriteLine($"[FSR] unavailable, rendering native: {e.Message}");
                    fsrUnavailable = true; wantUpscale = false; wantIW = outputW; wantIH = outputH;
                }
            }
            else
            {
                (wantIW, wantIH) = Dx12Upscaler.RenderResolutionFor(outputW, outputH, mode);
            }
        }

        if (target != null && wantIW == targetW && wantIH == targetH && fsrActive == wantUpscale && activeUpscaler == wantKind)
        {
            currentUpscaleMode = mode;
            return;
        }

        AllocateResolutionTargets(wantIW, wantIH);

        if (wantUpscale && wantKind == UpscalerKind.Dlss)
        {
            DisposeVendorUpscalers();
            dlss = Dx12DlssUpscaler.TryCreate(dev, wantIW, wantIH, outputW, outputH, Dx12Upscaler.DlssQuality(mode));
            if (dlss == null) { dlssUnavailable = true; EnsureUpscaleTargets(Dx12Upscaler.FsrEquivalent(mode)); return; }
        }
        else if (wantUpscale && wantKind == UpscalerKind.Xess)
        {
            DisposeVendorUpscalers();
            xess = Dx12XessUpscaler.TryCreate(dev, outputW, outputH, Dx12Upscaler.XessQuality(mode));
            if (xess == null) { xessUnavailable = true; EnsureUpscaleTargets(Dx12Upscaler.FsrEquivalent(mode)); return; }
        }
        else if (wantUpscale)
        {
            DisposeVendorUpscalers();
            try { fsr?.Dispose(); fsr = new Dx12FsrUpscaler(dev, wantIW, wantIH, outputW, outputH); }
            catch (Exception e)
            {
                Console.WriteLine($"[FSR] context create failed, rendering native: {e.Message}");
                fsrUnavailable = true; wantUpscale = false;
            }
        }
        else
        {
            DisposeVendorUpscalers();
        }

        fsrActive = wantUpscale;
        activeUpscaler = wantUpscale ? wantKind : UpscalerKind.Fsr;
        currentUpscaleMode = mode;
    }

    void DisposeVendorUpscalers()
    {
        dlss?.Dispose(); dlss = null;
        xess?.Dispose(); xess = null;
    }

    public override unsafe void Initialize()
    {
        outputW = targetW;
        outputH = targetH;
        target = new Dx12OffscreenTarget(dev, targetW, targetH, withDepth: false,
            colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
        ldr = new Dx12OffscreenTarget(dev, targetW, targetH, colorReadable: true);
        RegisterLdrUi();
        ldr.ColorToShaderResource();
        gbuffer = new Dx12GBuffer(dev, targetW, targetH);
        BuildRootSignature();
        BuildPipeline();
        BuildGeometryPass();

        cbSlotSize = (System.Runtime.InteropServices.Marshal.SizeOf<DrawConstants>() + 255) & ~255;
        cbSlotCount = 8192;
        cbFrameStride = (long)cbSlotSize * cbSlotCount;
        cbRing = dev.Device.CreateCommittedResource(
            HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(cbFrameStride * dev.FramesInFlight)), ResourceStates.GenericRead);
        cbMapped = cbRing.Map<byte>(0);

        customCbSlotSize = 256;
        customCbSlotCount = 1024;
        customCbFrameStride = (long)customCbSlotSize * customCbSlotCount;
        customCbRing = dev.Device.CreateCommittedResource(
            HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(customCbFrameStride * dev.FramesInFlight)), ResourceStates.GenericRead);
        customCbMapped = customCbRing.Map<byte>(0);

        srvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            cbSlotCount * MaterialSrvCount, shaderVisible: true, framesInFlight: dev.FramesInFlight);

        BuildSkinnedGeometryPass();

        ibl = new Dx12IblBaker(dev);
        skyLuts = new Dx12SkyLuts(dev);
        iblSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 3, shaderVisible: true,
            framesInFlight: dev.FramesInFlight);

        BuildShadows();

        frameCb = new Dx12FrameCb<FrameConstants>(dev);

        clusteredLights = new Dx12ClusteredLights(dev);

        gpuDrivenOn = Environment.GetEnvironmentVariable("BALLISTIC_DX12_GPUDRIVEN") != "0";
        hizWanted = gpuDrivenOn && Environment.GetEnvironmentVariable("BALLISTIC_DX12_GPUDRIVEN_HIZ") != "0";
        shadowCacheOn = Environment.GetEnvironmentVariable("BALLISTIC_DX12_SHADOW_CACHE") != "0";
        vsmWanted = Environment.GetEnvironmentVariable("BALLISTIC_DX12_VSM") == "1";
        skyTlutOn        = Environment.GetEnvironmentVariable("BALLISTIC_DX12_SKY_TLUT") != "0";
        exposureOverride = float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_EXPOSURE"),
                               System.Globalization.CultureInfo.InvariantCulture, out float exOv) ? exOv : 1.0e-5f;
        frameProfileOn   = Environment.GetEnvironmentVariable("BALLISTIC_DX12_FRAME_PROFILE") == "1";
        hizDebugOn       = Environment.GetEnvironmentVariable("BALLISTIC_DX12_HIZ_DEBUG") == "1";
        rtShadowsEnv     = Environment.GetEnvironmentVariable("BALLISTIC_DX12_RT_SHADOWS");
        rtShadowHardForce = Environment.GetEnvironmentVariable("BALLISTIC_DX12_RT_SHADOW_RAYS") == "1";
        fsrEnv           = Environment.GetEnvironmentVariable("BALLISTIC_DX12_FSR");
        shadowCacheDebugOn = Environment.GetEnvironmentVariable("BALLISTIC_DX12_SHADOW_CACHE_DEBUG") == "1";
        doors = Dx12RenderDoors.Resolve();
        if (doors.Minimal)
            Console.WriteLine("[DX12] BARE-MINIMUM render: G-buffer + deferred (sun/punctual) + composite only. " +
                              "Re-enable per pass with BALLISTIC_DX12_{SHADOWS,SKY,IBL,SSAO,BLOOM,AP,VOLUMES}=1 / BALLISTIC_FX_VOLUMETRIC=1.");
        gpuDriven = new Dx12GpuDrivenRenderer(dev);
        instanceCullOn = Environment.GetEnvironmentVariable("BALLISTIC_DX12_INSTANCE_CULL") == "1";
        if (instanceCullOn) instanceCuller = new Dx12InstanceCuller(dev);
        if (VisBufferOn && dev.HasMeshShaders)
        {
            visBuffer = new Dx12VisBufferPass(dev, gpuDriven);
            if (visBuffer.Available)
                Console.WriteLine("[DX12] VISIBILITY BUFFER geometry path ON (BALLISTIC_DX12_VISBUFFER=1) — " +
                                  "vis-id raster + deferred-material resolve replaces the GPU-driven G-buffer fill for whole-mesh geometry.");
            else { visBuffer.Dispose(); visBuffer = null; Console.WriteLine("[DX12] VISIBILITY BUFFER requested but pipeline build failed — falling back to the GPU-driven path."); }
        }

        SceneManager.RenderSetsCleared += OnRenderSetsCleared;

        AllocFsrOutput();

        dxr = new Dx12DxrShared(dev);

        graph = new Dx12RenderGraph(TimePass);
        deferredPass = new Dx12DeferredLightingPass(dev);
        graph.Add(deferredPass);
        gtaoPass = new Dx12GtaoPass(dev, targetW, targetH);
        graph.Add(gtaoPass);
        rtaoPass = new Dx12RtaoPass(dev, gtaoPass);
        graph.Add(rtaoPass);
        capsuleShadowPass = new Dx12CapsuleShadowPass(dev);
        graph.Add(capsuleShadowPass);
        skyPass = new Dx12SkyPass(dev);
        graph.Add(skyPass);
        apPass = new Dx12AerialPerspectivePass(dev);
        fogPass = new Dx12FogPass(dev, targetW, targetH);
        graph.Add(apPass);
        graph.Add(fogPass);
        transparentsPass = new Dx12TransparentsPass(dev);
        graph.Add(transparentsPass);
        ddgiPass = new Dx12DdgiPass(dev, targetW, targetH);
        graph.Add(ddgiPass);
        reflectionsPass = new Dx12ReflectionsPass(dev, targetW, targetH);
        graph.Add(reflectionsPass);
        taaPass = new Dx12TaaPass(dev, targetW, targetH);
        graph.Add(taaPass);
        fsrPass = new Dx12FsrPass(dev);
        graph.Add(fsrPass);
        motionBlurPass = new Dx12MotionBlurPass(dev, targetW, targetH);
        graph.Add(motionBlurPass);
        dofPass = new Dx12DepthOfFieldPass(dev, targetW, targetH);
        graph.Add(dofPass);
        compositePass = new Dx12CompositePass(dev, targetW, targetH);
        graph.Add(compositePass);
        graph.Add(new Dx12CullProbePass());
        graph.MarkCoreBoundary();
        graph.Build();
        graph.Compile();
        graphPath = Environment.GetEnvironmentVariable("BALLISTIC_DX12_GRAPH") != "0";
        if (graphPath)
        {
            Console.Error.WriteLine(
                "[DX12] Render graph (phase-2 V1): COMPILED ORDER active (default; BALLISTIC_DX12_GRAPH=0 to disable).");
            Console.Error.WriteLine(graph.LastCompileReport);
        }

        barriersPath = graphPath && Environment.GetEnvironmentVariable("BALLISTIC_DX12_GRAPH_BARRIERS") != "0";
        graph.SetBarriersDerived(barriersPath);
        if (barriersPath)
        {
            Console.Error.WriteLine(
                "[DX12] Render graph (phase-2 V3): AUTO-DERIVED BARRIERS active (default; BALLISTIC_DX12_GRAPH_BARRIERS=0 to disable).");
            Console.Error.WriteLine(graph.LastDeriverReport);
        }

        aliasPath = graphPath && Environment.GetEnvironmentVariable("BALLISTIC_DX12_GRAPH_ALIAS") != "0";
        if (aliasPath)
        {
            rtPool = new Dx12RenderTargetPool(dev);
            RegisterAliasPool(targetW, targetH);
            Dx12RenderTargetPool.Active = rtPool;
            graph.Resize(targetW, targetH);
            Console.Error.WriteLine(
                "[DX12] Render graph (phase-2 V2): TRANSIENT ALIASING active (BALLISTIC_DX12_GRAPH_ALIAS=1).");
            Console.Error.WriteLine(rtPool.PlanReport);
        }

        featureBlitter = new Dx12FeatureBlitter(dev, targetW, targetH);
        featureRecorder = new Dx12FeaturePassRecorder(featureBlitter);
        featureBridge = new Dx12RenderFeatureBridge(graph, featureRecorder);
    }

    Dx12GpuDrivenRenderer gpuDriven;
    Dx12InstanceCuller instanceCuller;

    bool instanceCullOn;

    readonly System.Collections.Generic.List<(Mesh mesh, Material mat, Matrix4x4[] transforms)> pendingInstanced = new();
    bool gpuDrivenOn;

    Dx12VisBufferPass visBuffer;
    bool? visBufferOnCached;
    bool VisBufferOn => visBufferOnCached ??= Environment.GetEnvironmentVariable("BALLISTIC_DX12_VISBUFFER") == "1";

    void OnRenderSetsCleared() {
        gpuDriven.Invalidate();
        hizPrimed = false;
        shadowMapEverRendered = false;
    }

    bool hizWanted;

    Dx12RenderDoors doors;

    public Dx12RenderDoors Doors {
        get => doors;
        set => doors = value;
    }
    public void SetDoor(string door, bool value) => doors = doors.With(door, value);
    Vector3 hizLastCamPos;
    bool hizPrimed;

    readonly System.Collections.Generic.List<IStaticMeshRenderer> wholeMeshRenderers = new();

    readonly System.Collections.Generic.List<IStaticMeshRenderer> splitMeshRenderers = new();
    readonly System.Collections.Generic.List<IStaticMeshRenderer> gpuDrivenGeometry = new();

    Dx12RenderGraph graph;

    Dx12FeatureBlitter featureBlitter;
    Dx12FeaturePassRecorder featureRecorder;

    Dx12RenderFeatureBridge featureBridge;

    bool graphPath;

    Dx12RenderTargetPool rtPool;

    bool aliasPath;

    bool barriersPath;

    bool skyTlutOn;
    float exposureOverride;
    bool frameProfileOn;
    bool hizDebugOn;
    string? rtShadowsEnv;
    bool rtShadowHardForce;
    string? fsrEnv;
    bool shadowCacheDebugOn;

    bool shadowCacheOn;
    readonly Matrix4x4[] lastCascadeMatrices = new Matrix4x4[MaxCascades];
    int lastCasterStamp;
    int lastActiveCascadeCount = -1;
    bool shadowMapEverRendered;

    static Vector2 JitterOffset(int frameIndex)
    {
        int i = (frameIndex % 8) + 1;
        return new Vector2(Halton(i, 2) - 0.5f, Halton(i, 3) - 0.5f);
    }

    static float Halton(int index, int b)
    {
        float r = 0f, f = 1f;
        while (index > 0)
        {
            f /= b;
            r += f * (index % b);
            index /= b;
        }

        return r;
    }

    unsafe void BuildGeometryPass()
    {
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView,
            new RootDescriptor1(0, 0), ShaderVisibility.All);
        var matRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, MaterialSrvCount,
            baseShaderRegister: 0);
        var matTable = new RootParameter1(new RootDescriptorTable1(matRange), ShaderVisibility.Pixel);
        var motionCbv = new RootParameter1(RootParameterType.ConstantBufferView,
            new RootDescriptor1(1, 0), ShaderVisibility.Pixel);
        var customCbv = new RootParameter1(RootParameterType.ConstantBufferView,
            new RootDescriptor1(2, 0), ShaderVisibility.Pixel);
        var customTexRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, MaxCustomTex,
            baseShaderRegister: 6);
        var customTexTable = new RootParameter1(new RootDescriptorTable1(customTexRange), ShaderVisibility.Pixel);
        var wrap = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 16,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        gbufferRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout,
                new[] { cbv, matTable, motionCbv, customCbv, customTexTable }, new[] { wrap })));

        motionCb = new Dx12FrameCb<MotionConstants>(dev);
        normalLodBiasCached = float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_NORMAL_LOD_BIAS"),
            System.Globalization.CultureInfo.InvariantCulture, out float nlbInit) ? nlbInit : 0.5f;

        bool useSkeleton = Environment.GetEnvironmentVariable("BALLISTIC_DX12_SURFACE_SKELETON") == "1";
        string shaderFile = useSkeleton ? "SurfaceSkeleton.hlsl" : "GBuffer.hlsl";
        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl(shaderFile);
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", shaderFile);
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", shaderFile);
        gbufferLayout = new InputLayoutDescription(
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 0, 1),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 0, 2),
            new InputElementDescription("TANGENT", 0, Format.R32G32B32A32_Float, 0, 3));
        gbufferVsBytecode = vs;
        gbufferPso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = gbufferRootSig, VertexShader = vs, PixelShader = ps, InputLayout = gbufferLayout,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullClockwise,
            BlendState = BlendDescription.Opaque, DepthStencilState = DepthStencilDescription.Default,
            RenderTargetFormats = Dx12GBuffer.ColorFormats,
            DepthStencilFormat = Dx12GBuffer.DepthFormat, SampleDescription = new SampleDescription(1, 0),
        });

        surfaceCache = new Dx12SurfaceShaderCache(dev.Device, gbufferRootSig, gbufferLayout, gbufferVsBytecode);
        if (Environment.GetEnvironmentVariable("BALLISTIC_DX12_SURFACE_SELFTEST") == "1")
            surfaceCache.SelfTest();
    }

    InputLayoutDescription gbufferLayout;
    byte[] gbufferVsBytecode;

    internal Dx12SurfaceShaderCache surfaceCache;

    Dx12SurfaceWatcher surfaceWatcher;
    bool surfaceWatcherTried;
    public event Action SurfaceShaderReloaded;

    void EnsureSurfaceWatcher() {
        var project = AssetDatabase.Project;
        if (surfaceWatcherTried || surfaceCache is null || project is null) return;
        surfaceWatcherTried = true;
        surfaceCache.FramesInFlight = dev.FramesInFlight;
        surfaceWatcher = new Dx12SurfaceWatcher(project.AssetsPath);
        if (Environment.GetEnvironmentVariable("BALLISTIC_DX12_SURFACE_HRDEBUG") == "1")
            Console.WriteLine($"[surface] watcher init on '{project.AssetsPath}'");
    }

    public override bool PollSurfaceReload() {
        EnsureSurfaceWatcher();
        return surfaceWatcher?.HasPending ?? false;
    }

    void ProcessSurfaceHotReload() {
        if (surfaceCache is null) return;
        surfaceCache.DrainDeferred(frameCounter);

        EnsureSurfaceWatcher();
        var project = AssetDatabase.Project;
        var changed = surfaceWatcher?.DrainPending();
        if (changed is null || project is null) return;
        if (Environment.GetEnvironmentVariable("BALLISTIC_DX12_SURFACE_HRDEBUG") == "1")
            Console.WriteLine($"[surface] drained {changed.Count} changed file(s)");

        bool any = false;
        foreach (string abs in changed) {
            string rel = project.ToAssetPath(abs);
            string body;
            try { body = System.IO.File.ReadAllText(abs); }
            catch (System.IO.IOException) { continue; }

            if (surfaceCache.Reload(rel, body, frameCounter) > 0) any = true;
        }
        if (any) SurfaceShaderReloaded?.Invoke();
    }

    static StandardShader CustomShaderOf(Material mat) =>
        mat?.Shader is StandardShader { HasCustomSurface: true } s ? s : null;

    unsafe void BindCustomProps(ID3D12GraphicsCommandList cl, Material mat, ShaderProperties props,
        Dx12Texture2D fallbackTex) {
        if (props is null) return;

        if (customDrawSlot < customCbSlotCount) {
            long baseOff = CustomCbFrameOffset + (long)customDrawSlot * customCbSlotSize;
            byte* dst = customCbMapped + baseOff;
            int cbIndex = 0;
            foreach (var p in props) {
                if (p.Semantic != MaterialSemantic.None) continue;
                switch (p.Type) {
                    case ShaderPropertyType.Texture2D: continue;
                    case ShaderPropertyType.Color:
                    case ShaderPropertyType.Vector: {
                        var v = mat.GetCustomVector(p.Name);
                        if ((cbIndex + 1) * 16 <= customCbSlotSize)
                            *(Vector4*)(dst + cbIndex * 16) = v;
                        cbIndex++;
                        break;
                    }
                    default: {
                        float f = mat.GetCustomFloat(p.Name);
                        if ((cbIndex + 1) * 16 <= customCbSlotSize)
                            *(float*)(dst + cbIndex * 16) = f;
                        cbIndex++;
                        break;
                    }
                }
            }
            if (cbIndex > 0)
                cl.SetGraphicsRootConstantBufferView(3, customCbRing.GPUVirtualAddress + (ulong)baseOff);
            customDrawSlot++;
        }

        int texCount = 0;
        foreach (var p in props)
            if (p.Semantic == MaterialSemantic.None && p.Type == ShaderPropertyType.Texture2D) texCount++;
        if (texCount > 0) {
            int n = Math.Min(texCount, MaxCustomTex);
            int tbl = srvVisible.AllocateRange(MaxCustomTex);
            int i = 0;
            foreach (var p in props) {
                if (p.Semantic != MaterialSemantic.None || p.Type != ShaderPropertyType.Texture2D) continue;
                if (i >= MaxCustomTex) break;
                BindSrv(tbl + i, mat.GetCustomTexture(p.Name), TextureType.Diffuse, fallbackTex);
                i++;
            }

            for (; i < MaxCustomTex; i++)
                BindSrv(tbl + i, null, TextureType.Diffuse, fallbackTex);
            cl.SetGraphicsRootDescriptorTable(4, srvVisible.Gpu(tbl));
        }
    }

    static bool RendererHasCustomSurface(IStaticMeshRenderer r) {
        Mesh mesh = r.SharedMesh;
        if (mesh is null) return false;
        int only = r.SubMeshIndex;
        int first = only >= 0 ? only : 0;
        int last = only >= 0 ? only : mesh.SubMeshes.Length - 1;
        for (int s = first; s <= last; s++) {
            if ((uint)s >= (uint)mesh.SubMeshes.Length) break;
            if (CustomShaderOf(r.MaterialFor(s)) is not null) return true;
        }
        return false;
    }

    bool SceneHasCustomSurface() {
        foreach (IStaticMeshRenderer r in RendererSet)
            if (r is { IsActive: true, IsRenderable: true } && RendererHasCustomSurface(r))
                return true;
        return false;
    }

    unsafe void BuildSkinnedGeometryPass()
    {
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView,
            new RootDescriptor1(0, 0), ShaderVisibility.All);
        var matRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, MaterialSrvCount,
            baseShaderRegister: 0);
        var matTable = new RootParameter1(new RootDescriptorTable1(matRange), ShaderVisibility.Pixel);
        var motionCbv = new RootParameter1(RootParameterType.ConstantBufferView,
            new RootDescriptor1(1, 0), ShaderVisibility.Pixel);
        var boneSrv = new RootParameter1(RootParameterType.ShaderResourceView,
            new RootDescriptor1(6, 0), ShaderVisibility.Vertex);
        var wrap = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 16,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        skinnedGbufferRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout,
                new[] { cbv, matTable, motionCbv, boneSrv }, new[] { wrap })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("GBufferSkinned.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "GBufferSkinned.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "GBufferSkinned.hlsl");
        var layout = new InputLayoutDescription(
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 0, 1),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 0, 2),
            new InputElementDescription("TANGENT", 0, Format.R32G32B32A32_Float, 0, 3),
            new InputElementDescription("BLENDINDICES", 0, Format.R32G32B32A32_Float, 0, 4),
            new InputElementDescription("BLENDWEIGHT", 0, Format.R32G32B32A32_Float, 0, 5));
        skinnedGbufferPso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = skinnedGbufferRootSig, VertexShader = vs, PixelShader = ps, InputLayout = layout,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullClockwise,
            BlendState = BlendDescription.Opaque, DepthStencilState = DepthStencilDescription.Default,
            RenderTargetFormats = Dx12GBuffer.ColorFormats,
            DepthStencilFormat = Dx12GBuffer.DepthFormat, SampleDescription = new SampleDescription(1, 0),
        });

        boneMatrixSlotSize = (MaxBonesPerDraw * 64 + 255) & ~255;
        boneMatrixSlotCount = 64;
        boneFrameStride = (long)boneMatrixSlotSize * boneMatrixSlotCount;
        boneMatrixRing = dev.Device.CreateCommittedResource(
            HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(boneFrameStride * dev.FramesInFlight)),
            ResourceStates.GenericRead);
        boneMatrixMapped = boneMatrixRing.Map<byte>(0);
    }

    unsafe void BuildShadows()
    {
        shadowMap = new Dx12ShadowMap(dev, shadowMapSize, MaxCascades);

        shadowRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout, new[]
            {
                new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0),
                    ShaderVisibility.Vertex)
            })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("ShadowDepth.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "ShadowDepth.hlsl");
        var layout = new InputLayoutDescription(
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0));
        var raster = RasterizerDescription.CullClockwise;
        raster.DepthBias = 2000;
        raster.SlopeScaledDepthBias = 2.5f;
        raster.DepthBiasClamp = 0f;
        var psoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = shadowRootSig, VertexShader = vs, PixelShader = default,
            InputLayout = layout, PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            SampleMask = uint.MaxValue, RasterizerState = raster, BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.Default,
            RenderTargetFormats = System.Array.Empty<Format>(),
            DepthStencilFormat = Dx12ShadowMap.DepthFormat,
            SampleDescription = new SampleDescription(1, 0),
        };
        shadowPso = dev.Device.CreateGraphicsPipelineState(psoDesc);

        shadowCbSlotSize = (System.Runtime.InteropServices.Marshal.SizeOf<ShadowConstants>() + 255) & ~255;
        shadowCbSlotCount = MaxCascades * 4096;
        shadowCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)((long)shadowCbSlotSize * shadowCbSlotCount * dev.FramesInFlight)),
            ResourceStates.GenericRead);
        shadowCbMapped = shadowCb.Map<byte>(0);
    }

    void BuildRootSignature()
    {
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView,
            new RootDescriptor1(0, 0), ShaderVisibility.All);
        var matRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, MaterialSrvCount,
            baseShaderRegister: 0);
        var matTable = new RootParameter1(new RootDescriptorTable1(matRange), ShaderVisibility.Pixel);
        var iblRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 4, baseShaderRegister: 6);
        var iblTable = new RootParameter1(new RootDescriptorTable1(iblRange), ShaderVisibility.Pixel);
        var frameCbv = new RootParameter1(RootParameterType.ConstantBufferView,
            new RootDescriptor1(1, 0), ShaderVisibility.Pixel);

        var wrap = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 16,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        var clamp = new StaticSamplerDescription(ShaderVisibility.Pixel, 1, 0)
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };

        var desc = new RootSignatureDescription1(
            RootSignatureFlags.AllowInputAssemblerInputLayout,
            new[] { cbv, matTable, iblTable, frameCbv }, new[] { wrap, clamp });
        rootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(desc));
    }

    void BuildPipeline()
    {
        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("StandardOpaque.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "StandardOpaque.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "StandardOpaque.hlsl");

        var layout = new InputLayoutDescription(
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 0, 1),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 0, 2),
            new InputElementDescription("TANGENT", 0, Format.R32G32B32A32_Float, 0, 3));

        var psoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = rootSig,
            VertexShader = vs,
            PixelShader = ps,
            InputLayout = layout,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullClockwise,
            BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.Default,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat },
            DepthStencilFormat = Dx12OffscreenTarget.DepthFormat,
            SampleDescription = new SampleDescription(1, 0),
        };
        pso = dev.Device.CreateGraphicsPipelineState(psoDesc);
    }

    readonly System.Diagnostics.Stopwatch cpuFrameSw = new();

    readonly System.Diagnostics.Stopwatch passSw = new();
    bool? passTimingOn;

    bool? pp1InlineSyncsOn;
    bool Pp1InlineSyncs => pp1InlineSyncsOn ??=
        Environment.GetEnvironmentVariable("BALLISTIC_DX12_PP1_INLINE_SYNCS") != "0";

    bool? cpuBindlessOn;
    bool CpuBindless => cpuBindlessOn ??=
        Environment.GetEnvironmentVariable("BALLISTIC_DX12_CPU_BINDLESS") == "1";

    static System.Collections.Generic.IReadOnlyCollection<IStaticMeshRenderer> RendererSet =>
        FrameSnapshot.IsRenderThreadDrawing
            ? (System.Collections.Generic.IReadOnlyCollection<IStaticMeshRenderer>)FrameSnapshot.RenderSet
            : RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection;

    static System.Numerics.Vector3? NearestInstanceWorldPos(Mesh mesh)
    {
        foreach (IStaticMeshRenderer r in RendererSet) {
            if (r is null || !r.IsActive || r.SharedMesh != mesh) continue;
            var p = r.Transform.RenderWorldPosition;
            return new System.Numerics.Vector3(p.X, p.Y, p.Z);
        }
        return null;
    }

    bool? gpuDrivenSplitOn;
    bool GpuDrivenSplit => gpuDrivenSplitOn ??=
        Environment.GetEnvironmentVariable("BALLISTIC_DX12_GPUDRIVEN_SPLIT") != "0";

    bool? lodOn;
    bool LodEnabled => lodOn ??= Environment.GetEnvironmentVariable("BALLISTIC_DX12_LOD") != "0";

    bool? gpuDrivenSkinnedOn;
    bool GpuDrivenSkinned => gpuDrivenSkinnedOn ??=
        Environment.GetEnvironmentVariable("BALLISTIC_DX12_GPUDRIVEN_SKINNED") == "1";

    bool? meshletsOn;
    bool MeshletsEnabled => meshletsOn ??=
        Environment.GetEnvironmentVariable("BALLISTIC_DX12_MESHLETS") == "1";

    int motionYawFrame;
    float? motionYawCached;
    float MotionYawPerFrame => motionYawCached ??= (float.TryParse(
        Environment.GetEnvironmentVariable("BALLISTIC_DX12_GI_MOTION_YAW"),
        System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f);

    bool PassTimingEnabled => passTimingOn ??= (Environment.GetEnvironmentVariable("BALLISTIC_DX12_PASS_TIMING") == "1"
                                                || !string.IsNullOrWhiteSpace(
                                                    Environment.GetEnvironmentVariable("BALLISTIC_STATS_OUT")));

    void TimePass(string name, Action body)
    {
        if (!PassTimingEnabled)
        {
            body();
            return;
        }

        if (dev.GpuTimerAvailable)
        {
            dev.GpuTimerBegin();
            body();
            double gpuMs = dev.GpuTimerEnd();
            RenderStats.Scene.GpuPasses.Add((name, gpuMs));
            return;
        }

        passSw.Restart();
        body();
        passSw.Stop();
        RenderStats.Scene.GpuPasses.Add((name, passSw.Elapsed.TotalMilliseconds));
    }

    public override unsafe RenderMetrics BeginRender(RendererArgs args)
    {
        IViewProjectionProvider vp = args.viewProjectionProvider;
        if (vp is null || target is null)
            return default;

        ProcessSurfaceHotReload();

        cpuFrameSw.Restart();
        if (PassTimingEnabled) RenderStats.Scene.GpuPasses.Clear();

        LodSettings.Enabled = LodEnabled;
        LodSettings.FreezeForDeterminism = DeterministicCapture;
        if (LodEnabled) {
            LodSettings.GlobalBias = float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_LOD_BIAS"),
                System.Globalization.CultureInfo.InvariantCulture, out float lb) ? lb : 1f;
            LodSettings.ForceLod = int.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_LOD_FORCE"), out int fl) ? fl : -1;
        }

        EnsureUpscaleTargets(ResolveUpscaleMode());

        Matrix4x4 view = vp.GetViewMatrix();
        float motionYaw = MotionYawPerFrame;
        if (motionYaw != 0f)
        {
            float ang = motionYaw * (3.14159265f / 180f) * motionYawFrame;
            view = view * Matrix4x4.CreateRotationY(ang);
            motionYawFrame++;
        }
        Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(
            FovYRadians, (float)targetW / targetH, CameraNear, CameraFar);
        Matrix4x4 projUnjittered = proj;
        Matrix4x4 viewProjUnjittered = view * proj;

        if (MeshUploadQueue.HasPending) {
            Vector3 streamCamPos = Matrix4x4.Invert(view, out Matrix4x4 invView) ? invView.Translation : Vector3.Zero;
            MeshUploadQueue.PumpUploads(NearestInstanceWorldPos, streamCamPos);
        }

        bool taaOn = PostFX.TaaEnabled && !fsrActive && !DeterministicCapture && !doors.Minimal;
        bool jitterOn = taaOn || fsrActive;
        currentJitter = jitterOn ? JitterOffset(taaFrame) : Vector2.Zero;
        if (jitterOn)
        {
            proj.M31 += 2f * currentJitter.X / targetW;
            proj.M32 -= 2f * currentJitter.Y / targetH;
        }

        Matrix4x4 viewProj = view * proj;

        Matrix4x4 viewProjPrevForMotion = motionPrevValid ? motionPrevViewProj : viewProjUnjittered;
        var motionConstants = new MotionConstants
        {
            ViewProjCur = Matrix4x4.Transpose(viewProjUnjittered),
            ViewProjPrev = Matrix4x4.Transpose(viewProjPrevForMotion),
            NormalLodBias = normalLodBiasCached,
        };

        Vector3 camPos = vp.Transform.WorldPosition;

        if (doors.Volumes)
        {
            VolumeManager.Update(camPos);
            VolumePostProcessing.Apply(VolumeManager.Stack, PostFX);
        }

        LightUniforms light = LightUniforms.Resolve();
        Vector3 lightDir = light.Direction;
        Vector3 lightColor = light.Color;
        if (skyTlutOn && ProceduralSky.Active is { } skyForSun && lightDir.LengthSquared() > 1e-8f)
        {
            var st = skyForSun.SunTransmittance(new System.Numerics.Vector3(lightDir.X, lightDir.Y, lightDir.Z));
            lightColor *= new Vector3(st.X, st.Y, st.Z);
        }

        Vector3 ambient = vp.AmbientColor * light.AmbientIntensity;
        float exposure = exposureOverride;

        wholeMeshRenderers.Clear();
        splitMeshRenderers.Clear();
        if (gpuDrivenOn)
        {
            bool split = GpuDrivenSplit;
            foreach (IStaticMeshRenderer r in RendererSet)
            {
                if (r is not { IsActive: true, IsRenderable: true } || r.SharedMesh == null) continue;
                if (RendererHasCustomSurface(r)) continue;
                if (r.SubMeshIndex < 0) wholeMeshRenderers.Add(r);
                else if (split && !r.IsSkinned) splitMeshRenderers.Add(r);
            }
        }

        gpuDrivenGeometry.Clear();
        gpuDrivenGeometry.AddRange(wholeMeshRenderers);
        gpuDrivenGeometry.AddRange(splitMeshRenderers);

        bool fprof = frameProfileOn;
        var fpsw = fprof ? System.Diagnostics.Stopwatch.StartNew() : null;
        long fpGc = fprof ? GC.GetTotalAllocatedBytes() : 0;
        void FP(string t) { if (fprof) { fpsw.Stop(); long g = GC.GetTotalAllocatedBytes(); Console.WriteLine($"[FrameProf] {t} {fpsw.Elapsed.TotalMilliseconds:0.00}ms alloc={g-fpGc}B"); fpGc = g; fpsw.Restart(); } }

        var prof = dev.GpuProfiler;
        void GpuMark(string name) { if (prof.Enabled && dev.FrameList is { } fl) prof.Begin(fl, name); }
        void GpuMarkEnd() { if (prof.Enabled && dev.FrameList is { } fl) prof.End(fl); }

        iblActiveThisFrame = false;
        if (doors.Ibl && ProceduralSky.Active is { } pSky)
        {
            Vector3 sunDir = lightDir.LengthSquared() < 1e-8f ? Vector3.UnitY : Vector3.Normalize(lightDir);
            float sunAngR = (DirectionalLight.Instance?.AngularDiameter ?? 0.53f) * 0.5f * (MathF.PI / 180f);
            skyLuts.EnsureBaked(pSky.AirDensity, pSky.Haze, pSky.OzoneDensity);
            ibl.EnsureBaked(pSky, sunDir, lightColor, sunAngR);
            iblActiveThisFrame = ibl.HasBaked;
        }
        FP("IBL bake");

        ddgiPass?.RunPendingPlacement();

        dev.BeginFrame();

        GpuMark("Shadows");
        vsmActiveThisFrame = doors.Shadows && (vsmWanted || (doors.Volumes && PostFX.UseVirtualShadowMaps));
        if (vsmActiveThisFrame)
            RenderVsm(camPos, light);
        else if (doors.Shadows)
            RenderShadows(view, projUnjittered, light);
        else
            shadowsThisFrame = false;
        GpuMarkEnd();
        GpuMark("Geometry+Deferred");
        FP("RenderShadows");

        motionCb.Write(motionConstants);

        var fc = new FrameConstants
        {
            Cascade0 = Matrix4x4.Transpose(cascadeMatrices[0]),
            Cascade1 = Matrix4x4.Transpose(cascadeMatrices[1]),
            Cascade2 = Matrix4x4.Transpose(cascadeMatrices[2]),
            Cascade3 = Matrix4x4.Transpose(cascadeMatrices[3]),
            CascadeBias = new Vector4(0.0015f, 0.0020f, 0.0030f, 0.0050f),
            CascadeCountF = activeCascadeCount, ShadowsEnabled = shadowsThisFrame ? 1f : 0f,
            ShadowMapTexel = 1f / shadowMapSize,
            CascadeBlend = Math.Clamp(PostFX.ShadowCascadeBlend, 0f, 0.5f),
            ShadowFiltering = PostFX.ShadowFiltering,
            ShadowSoftness = PostFX.ShadowSoftness,
            ContactShadowsOn = PostFX.ContactShadowsEnabled ? 1f : 0f,
            ContactShadowLength = PostFX.ContactShadowLength,
            ContactShadowSteps = PostFX.ContactShadowSteps,
            ContactShadowThickness = PostFX.ContactShadowThickness,
        };
        frameCb.Write(fc);

        int draws = 0;
        int culled = 0;
        long tris = 0;
        srvVisible.Reset();
        int slot = 0;
        customDrawSlot = 0;

        ExtractFrustumPlanes(viewProjUnjittered);

        if (gpuDrivenOn || CpuBindless)
            gpuDriven.EnsureMaterialTable(gpuDrivenGeometry);

        bool cpuBindless = CpuBindless;
        if (cpuBindless && SceneHasCustomSurface())
            cpuBindless = false;
        if (cpuBindless)
        {
            bool allRegistered = true;
            foreach (IStaticMeshRenderer r in RendererSet)
            {
                if (r is null || !r.IsActive || !r.IsRenderable) continue;
                if (gpuDrivenOn && r.SubMeshIndex < 0) continue;
                if (r.IsSkinned) continue;
                Mesh m = r.SharedMesh; if (m is null) continue;
                int only = r.SubMeshIndex;
                int f = only >= 0 ? only : 0;
                int l = only >= 0 ? only : m.SubMeshes.Length - 1;
                for (int s = f; s <= l; s++)
                {
                    if ((uint)s >= (uint)m.SubMeshes.Length) break;
                    Material mm = r.MaterialFor(s);
                    if (mm is null || mm.Transparent) continue;
                    if (gpuDriven.ResolveOrRegisterMaterialId(mm) < 0) { allRegistered = false; break; }
                }
                if (!allRegistered) break;
            }
            if (!allRegistered)
            {
                cpuBindless = false;
                Debugging.Log("[R2] bindless material table full — CPU opaque path fell back to descriptor tables this frame.");
            }
        }

        bool hizEnabled = false;
        if (hizWanted && wholeMeshRenderers.Count > 0)
        {
            float camDelta = (camPos - hizLastCamPos).Length();
            hizEnabled = hizPrimed && camDelta < 2.0f;
            hizLastCamPos = camPos;
            hizPrimed = true;
            gbuffer.DepthToNonPixelShaderResource();
            gpuDriven.BuildHiZ(gbuffer.DepthSrvCpu, targetW, targetH, hizEnabled);
        }

        var fallbackDiffuse = DefaultTextures.Neutral(TextureType.Diffuse) as Dx12Texture2D;

        int cpuDrawIndex = 0;
        bool skinnedBindlessBound = false;

        gbuffer.RenderGeometry(cl =>
        {
            if (cpuBindless)
            {
                gpuDriven.CpuBindlessBegin(cl, motionCb.Gpu);
            }
            else
            {
                cl.SetGraphicsRootSignature(gbufferRootSig);
                cl.SetPipelineState(gbufferPso);
                cl.SetDescriptorHeaps(srvVisible.Heap);
                cl.SetGraphicsRootConstantBufferView(2, motionCb.Gpu);
            }
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

            foreach (IStaticMeshRenderer r in RendererSet)
            {
                if (r is null || !r.IsActive || !r.IsRenderable) continue;
                bool customSurfaceR = RendererHasCustomSurface(r);
                if (!customSurfaceR && gpuDrivenOn && r.SubMeshIndex < 0) continue;
                if (!customSurfaceR && gpuDrivenOn && GpuDrivenSplit && r.SubMeshIndex >= 0 && !r.IsSkinned) continue;
                if (r.IsSkinned) continue;
                Mesh mesh = r.SharedMesh;
                if (mesh is null) continue;

                var vb = mesh.VertexBuffer as Dx12Buffer<GLVector3>;
                var ib = mesh.IndexBuffer as Dx12IndexBuffer;
                var nb = mesh.NormalBuffer as Dx12Buffer<GLVector3>;
                var ub = mesh.UvBuffer as Dx12Buffer<Vector2>;
                var tb = mesh.TangentBuffer as Dx12Buffer<Vector4>;
                if (vb?.Resource is null || ib?.Resource is null ||
                    nb?.Resource is null || ub?.Resource is null || tb?.Resource is null) continue;

                Matrix4x4 model = r.Transform.RenderMatrix;
                Matrix4x4 mvp = model * viewProj;

                Span<VertexBufferView> vbViews = stackalloc VertexBufferView[4];
                vbViews[0] = new VertexBufferView(vb.GpuAddress, (uint)vb.ByteSize, (uint)vb.Stride);
                vbViews[1] = new VertexBufferView(nb.GpuAddress, (uint)nb.ByteSize, (uint)nb.Stride);
                vbViews[2] = new VertexBufferView(ub.GpuAddress, (uint)ub.ByteSize, (uint)ub.Stride);
                vbViews[3] = new VertexBufferView(tb.GpuAddress, (uint)tb.ByteSize, (uint)tb.Stride);
                cl.IASetVertexBuffers(0, vbViews);
                cl.IASetIndexBuffer(new IndexBufferView(ib.GpuAddress, (uint)ib.ByteSize, Format.R32_UInt));

                int only = r.SubMeshIndex;
                int first = only >= 0 ? only : 0;
                int last = only >= 0 ? only : mesh.SubMeshes.Length - 1;

                for (int s = first; s <= last; s++)
                {
                    if ((uint)s >= (uint)mesh.SubMeshes.Length) break;
                    SubMeshData sub = mesh.SubMeshes[s];
                    if (sub.IndexCount <= 0) continue;
                    mesh.GetSubMeshBounds(s, out GLVector3 lmin, out GLVector3 lmax);
                    if (!AabbInFrustum(lmin, lmax, model))
                    {
                        culled++;
                        continue;
                    }

                    Material mat = r.MaterialFor(s);
                    if (mat is null) continue;
                    if (mat.Transparent) continue;
                    if (slot >= cbSlotCount) break;

                    bool drewBindless = false;
                    if (cpuBindless)
                    {
                        int mid = gpuDriven.TryMaterialId(mat, out int rid) ? rid : -1;
                        if (mid >= 0 && gpuDriven.CpuBindlessWrite(cpuDrawIndex, mvp, model, mid))
                        {
                            cl.SetGraphicsRoot32BitConstant(0, (uint)cpuDrawIndex, 0);
                            cl.DrawIndexedInstanced((uint)sub.IndexCount, 1, (uint)sub.IndexStart, 0, 0);
                            cpuDrawIndex++;
                            draws++; tris += sub.IndexCount / 3;
                            drewBindless = true;
                        }
                        else
                        {
                            drewBindless = true;
                        }
                    }
                    if (!drewBindless)
                    {
                        bool hasMetal = mat.Metallic is not null;
                        bool hasRough = mat.Roughness is not null;
                        bool emissive = mat.IsEmissive;
                        var ec = mat.GetVector(MaterialSemantic.EmissiveColor);
                        var c = new DrawConstants
                        {
                            Mvp = Matrix4x4.Transpose(mvp),
                            Model = Matrix4x4.Transpose(model),
                            LightDir = lightDir, Exposure = exposure,
                            LightColor = lightColor, Metallic = mat.GetFloat(MaterialSemantic.MetallicFactor),
                            Ambient = ambient, Roughness = mat.GetFloat(MaterialSemantic.RoughnessFactor),
                            CameraPos = camPos, SpecularReflectance = mat.GetFloat(MaterialSemantic.SpecularReflectance),
                            BaseColorFactor = mat.GetVector(MaterialSemantic.BaseColorFactor),
                            EmissiveFactor = new Vector3(ec.X, ec.Y, ec.Z) * mat.GetFloat(MaterialSemantic.EmissiveIntensity),
                            HasEmissive = emissive ? 1f : 0f,
                            NormalStrength = mat.GetFloat(MaterialSemantic.NormalStrength),
                            NormalFlipY = mat.GetFloat(MaterialSemantic.NormalFlipY),
                            HasMetallicMap = hasMetal ? 1f : 0f, HasRoughnessMap = hasRough ? 1f : 0f,
                            PackedOrm = mat.GetFloat(MaterialSemantic.PackedOrm), Cutout = mat.GetFloat(MaterialSemantic.Cutout),
                            UseIBL = iblActiveThisFrame ? 1f : 0f,
                            PrefilterMaxMip = ibl != null ? ibl.PrefilterMipCount - 1 : 0f,
                        };
                        *(DrawConstants*)(cbMapped + CbFrameOffset + (long)slot * cbSlotSize) = c;
                        cl.SetGraphicsRootConstantBufferView(0,
                            cbRing.GPUVirtualAddress + (ulong)(CbFrameOffset + (long)slot * cbSlotSize));

                        int tableStart = srvVisible.AllocateRange(MaterialSrvCount);
                        BindSrv(tableStart + 0, mat.GetTexture(MaterialSemantic.DiffuseMap), TextureType.Diffuse, fallbackDiffuse);
                        BindSrv(tableStart + 1, mat.GetTexture(MaterialSemantic.NormalMap), TextureType.Normal, null);
                        BindSrv(tableStart + 2, mat.GetTexture(MaterialSemantic.MetallicMap), TextureType.Metallic, null);
                        BindSrv(tableStart + 3, mat.GetTexture(MaterialSemantic.RoughnessMap), TextureType.Roughness, null);
                        BindSrv(tableStart + 4, mat.GetTexture(MaterialSemantic.AOMap), TextureType.AO, null);
                        BindSrv(tableStart + 5, mat.GetTexture(MaterialSemantic.EmissiveMap), TextureType.Emissive, null);
                        cl.SetGraphicsRootDescriptorTable(1, srvVisible.Gpu(tableStart));

                        var css = CustomShaderOf(mat);
                        if (css is not null) {
                            var entry = surfaceCache.GetOrCompile(css.SurfaceSource, css.SurfaceKey,
                                css.SurfaceSourcePath, css.Properties);
                            if (entry.Pso is not null) cl.SetPipelineState(entry.Pso);
                            BindCustomProps(cl, mat, css.Properties, fallbackDiffuse);
                            cl.DrawIndexedInstanced((uint)sub.IndexCount, 1, (uint)sub.IndexStart, 0, 0);
                            cl.SetPipelineState(gbufferPso);
                        }
                        else {
                            cl.DrawIndexedInstanced((uint)sub.IndexCount, 1, (uint)sub.IndexStart, 0, 0);
                        }
                        draws++; tris += sub.IndexCount / 3;
                    }
                    slot++;
                }
            }

            int boneSlot = 0;
            bool skinnedStateSet = false;
            foreach (IStaticMeshRenderer r in RendererSet)
            {
                if (r is null || !r.IsActive || !r.IsRenderable || !r.IsSkinned) continue;
                Mesh mesh = r.SharedMesh;
                if (mesh is null || !mesh.IsSkinned) continue;
                if (boneSlot >= boneMatrixSlotCount || slot >= cbSlotCount) break;

                var vb = mesh.VertexBuffer as Dx12Buffer<GLVector3>;
                var ib = mesh.IndexBuffer as Dx12IndexBuffer;
                var nb = mesh.NormalBuffer as Dx12Buffer<GLVector3>;
                var ub = mesh.UvBuffer as Dx12Buffer<Vector2>;
                var tb = mesh.TangentBuffer as Dx12Buffer<Vector4>;
                var bib = mesh.BoneIndexBuffer as Dx12Buffer<Vector4>;
                var bwb = mesh.BoneWeightBuffer as Dx12Buffer<Vector4>;
                if (vb?.Resource is null || ib?.Resource is null || nb?.Resource is null ||
                    ub?.Resource is null || tb?.Resource is null ||
                    bib?.Resource is null || bwb?.Resource is null) continue;

                Matrix4[] skin = r.SkinningMatrices;
                int boneCount = skin is null ? 0 : System.Math.Min(skin.Length, MaxBonesPerDraw);
                byte* dst = boneMatrixMapped + BoneFrameOffset + (long)boneSlot * boneMatrixSlotSize;
                var mptr = (Matrix4x4*)dst;
                for (int b = 0; b < boneCount; b++)
                    mptr[b] = Matrix4x4.Transpose(skin[b]);
                ulong boneGpuAddr = boneMatrixRing.GPUVirtualAddress + (ulong)(BoneFrameOffset + (long)boneSlot * boneMatrixSlotSize);

                if (GpuDrivenSkinned && cpuBindless)
                {
                    int vcount = vb.ElementCount;
                    Matrix4x4 sModel = r.Transform.RenderMatrix;
                    Matrix4x4 sMvp = sModel * viewProj;
                    cl.ResourceBarrierTransition(vb.Resource, ResourceStates.VertexAndConstantBuffer, ResourceStates.NonPixelShaderResource);
                    cl.ResourceBarrierTransition(nb.Resource, ResourceStates.VertexAndConstantBuffer, ResourceStates.NonPixelShaderResource);
                    cl.ResourceBarrierTransition(tb.Resource, ResourceStates.VertexAndConstantBuffer, ResourceStates.NonPixelShaderResource);
                    cl.ResourceBarrierTransition(bib.Resource, ResourceStates.VertexAndConstantBuffer, ResourceStates.NonPixelShaderResource);
                    cl.ResourceBarrierTransition(bwb.Resource, ResourceStates.VertexAndConstantBuffer, ResourceStates.NonPixelShaderResource);
                    var skb = gpuDriven.DispatchSkin(cl, r, boneSlot, boneGpuAddr,
                        vb.GpuAddress, nb.GpuAddress, tb.GpuAddress, bib.GpuAddress, bwb.GpuAddress, vcount);
                    cl.ResourceBarrierTransition(vb.Resource, ResourceStates.NonPixelShaderResource, ResourceStates.VertexAndConstantBuffer);
                    cl.ResourceBarrierTransition(nb.Resource, ResourceStates.NonPixelShaderResource, ResourceStates.VertexAndConstantBuffer);
                    cl.ResourceBarrierTransition(tb.Resource, ResourceStates.NonPixelShaderResource, ResourceStates.VertexAndConstantBuffer);
                    cl.ResourceBarrierTransition(bib.Resource, ResourceStates.NonPixelShaderResource, ResourceStates.VertexAndConstantBuffer);
                    cl.ResourceBarrierTransition(bwb.Resource, ResourceStates.NonPixelShaderResource, ResourceStates.VertexAndConstantBuffer);
                    if (skb != null)
                    {
                        if (!skinnedBindlessBound) { gpuDriven.CpuBindlessBegin(cl, motionCb.Gpu); cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList); skinnedBindlessBound = true; }
                        if (DrawSkinnedBindless(cl, r, mesh, skb, ub, ib, sModel, sMvp, ref slot, ref draws, ref tris, ref cpuDrawIndex))
                        {
                            boneSlot++;
                            continue;
                        }
                    }
                }

                if (!skinnedStateSet)
                {
                    cl.SetGraphicsRootSignature(skinnedGbufferRootSig);
                    cl.SetPipelineState(skinnedGbufferPso);
                    cl.SetDescriptorHeaps(srvVisible.Heap);
                    cl.SetGraphicsRootConstantBufferView(2, motionCb.Gpu);
                    cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                    skinnedStateSet = true;
                }

                cl.SetGraphicsRootShaderResourceView(3, boneGpuAddr);

                Matrix4x4 model = r.Transform.RenderMatrix;
                Matrix4x4 mvp = model * viewProj;

                Span<VertexBufferView> sViews = stackalloc VertexBufferView[6];
                sViews[0] = new VertexBufferView(vb.GpuAddress, (uint)vb.ByteSize, (uint)vb.Stride);
                sViews[1] = new VertexBufferView(nb.GpuAddress, (uint)nb.ByteSize, (uint)nb.Stride);
                sViews[2] = new VertexBufferView(ub.GpuAddress, (uint)ub.ByteSize, (uint)ub.Stride);
                sViews[3] = new VertexBufferView(tb.GpuAddress, (uint)tb.ByteSize, (uint)tb.Stride);
                sViews[4] = new VertexBufferView(bib.GpuAddress, (uint)bib.ByteSize, (uint)bib.Stride);
                sViews[5] = new VertexBufferView(bwb.GpuAddress, (uint)bwb.ByteSize, (uint)bwb.Stride);
                cl.IASetVertexBuffers(0, sViews);
                cl.IASetIndexBuffer(new IndexBufferView(ib.GpuAddress, (uint)ib.ByteSize, Format.R32_UInt));

                int sOnly = r.SubMeshIndex;
                int sFirst = sOnly >= 0 ? sOnly : 0;
                int sLast = sOnly >= 0 ? sOnly : mesh.SubMeshes.Length - 1;
                for (int s = sFirst; s <= sLast; s++)
                {
                    if ((uint)s >= (uint)mesh.SubMeshes.Length) break;
                    SubMeshData sub = mesh.SubMeshes[s];
                    if (sub.IndexCount <= 0) continue;
                    Material mat = r.MaterialFor(s);
                    if (mat is null) continue;
                    if (mat.Transparent) continue;
                    if (slot >= cbSlotCount) break;

                    bool hasMetal = mat.Metallic is not null;
                    bool hasRough = mat.Roughness is not null;
                    bool emissive = mat.IsEmissive;
                    var ec = mat.GetVector(MaterialSemantic.EmissiveColor);
                    var c = new DrawConstants
                    {
                        Mvp = Matrix4x4.Transpose(mvp),
                        Model = Matrix4x4.Transpose(model),
                        LightDir = lightDir, Exposure = exposure,
                        LightColor = lightColor, Metallic = mat.GetFloat(MaterialSemantic.MetallicFactor),
                        Ambient = ambient, Roughness = mat.GetFloat(MaterialSemantic.RoughnessFactor),
                        CameraPos = camPos, SpecularReflectance = mat.GetFloat(MaterialSemantic.SpecularReflectance),
                        BaseColorFactor = mat.GetVector(MaterialSemantic.BaseColorFactor),
                        EmissiveFactor = new Vector3(ec.X, ec.Y, ec.Z) * mat.GetFloat(MaterialSemantic.EmissiveIntensity),
                        HasEmissive = emissive ? 1f : 0f,
                        NormalStrength = mat.GetFloat(MaterialSemantic.NormalStrength),
                        NormalFlipY = mat.GetFloat(MaterialSemantic.NormalFlipY),
                        HasMetallicMap = hasMetal ? 1f : 0f, HasRoughnessMap = hasRough ? 1f : 0f,
                        PackedOrm = mat.GetFloat(MaterialSemantic.PackedOrm), Cutout = mat.GetFloat(MaterialSemantic.Cutout),
                        UseIBL = iblActiveThisFrame ? 1f : 0f,
                        PrefilterMaxMip = ibl != null ? ibl.PrefilterMipCount - 1 : 0f,
                    };
                    *(DrawConstants*)(cbMapped + CbFrameOffset + (long)slot * cbSlotSize) = c;
                    cl.SetGraphicsRootConstantBufferView(0,
                        cbRing.GPUVirtualAddress + (ulong)(CbFrameOffset + (long)slot * cbSlotSize));

                    int tableStart = srvVisible.AllocateRange(MaterialSrvCount);
                    BindSrv(tableStart + 0, mat.GetTexture(MaterialSemantic.DiffuseMap), TextureType.Diffuse, fallbackDiffuse);
                    BindSrv(tableStart + 1, mat.GetTexture(MaterialSemantic.NormalMap), TextureType.Normal, null);
                    BindSrv(tableStart + 2, mat.GetTexture(MaterialSemantic.MetallicMap), TextureType.Metallic, null);
                    BindSrv(tableStart + 3, mat.GetTexture(MaterialSemantic.RoughnessMap), TextureType.Roughness, null);
                    BindSrv(tableStart + 4, mat.GetTexture(MaterialSemantic.AOMap), TextureType.AO, null);
                    BindSrv(tableStart + 5, mat.GetTexture(MaterialSemantic.EmissiveMap), TextureType.Emissive, null);
                    cl.SetGraphicsRootDescriptorTable(1, srvVisible.Gpu(tableStart));

                    cl.DrawIndexedInstanced((uint)sub.IndexCount, 1, (uint)sub.IndexStart, 0, 0);
                    draws++;
                    tris += sub.IndexCount / 3;
                    slot++;
                }

                boneSlot++;
            }

            if (skinnedStateSet)
            {
                cl.SetGraphicsRootSignature(gbufferRootSig);
                cl.SetPipelineState(gbufferPso);
                cl.SetGraphicsRootConstantBufferView(2, motionCb.Gpu);
            }

            if (gpuDrivenOn && gpuDrivenGeometry.Count > 0 && visBuffer == null)
            {
                bool drewMeshlet = false;
                if (MeshletsEnabled && gpuDriven.MeshletAvailable)
                {
                    var cl6 = cl.QueryInterfaceOrNull<ID3D12GraphicsCommandList6>();
                    if (cl6 != null)
                    {
                        bool coneCull = Environment.GetEnvironmentVariable("BALLISTIC_DX12_MESHLET_CONE") != "0";
                        draws += gpuDriven.RenderIntoMeshlet(cl6, gpuDrivenGeometry, viewProj, frustumPlanes,
                            new Vector3(camPos.X, camPos.Y, camPos.Z), coneCull,
                            viewProjUnjittered, view, CameraNear, CameraFar, motionCb.Gpu, ref cpuDrawIndex);
                        tris += gpuDriven.MeshletTris;
                        cl6.Dispose();
                        drewMeshlet = true;
                    }
                }
                if (!drewMeshlet)
                {
                    draws += gpuDriven.RenderInto(cl, gpuDrivenGeometry, viewProj, frustumPlanes,
                        viewProjUnjittered, view, CameraNear, CameraFar, motionCb.Gpu);
                    tris += gpuDriven.LastTris;
                }
            }

            if (instanceCuller != null)
            {
                InstanceCullSelfTest(viewProj, viewProjUnjittered, view);
                FlushInstanced(cl, viewProj, viewProjUnjittered, view);
            }
        });

        if (visBuffer != null && gpuDrivenGeometry.Count > 0)
        {
            bool coneCull = Environment.GetEnvironmentVariable("BALLISTIC_DX12_MESHLET_CONE") != "0";
            int visDraws = visBuffer.Render(gbuffer, gpuDrivenGeometry, viewProj, frustumPlanes,
                new Vector3(camPos.X, camPos.Y, camPos.Z), coneCull, viewProjUnjittered, view,
                CameraNear, CameraFar, viewProjUnjittered, viewProjPrevForMotion, 0f);
            draws += visDraws;
        }

        if (gpuDrivenOn && wholeMeshRenderers.Count > 0 && hizDebugOn)
        {
            var (vis, tot) = gpuDriven.DebugVisibleCount();
            Console.WriteLine($"[HiZDebug] visible submeshes {vis}/{tot} (hizEnabled={(hizEnabled ? 1 : 0)})");
        }

        GatherPunctualLights(view, proj);

        rtShadowsThisFrame = false;
        string? rtsEnv = rtShadowsEnv;
        bool rtShadowsWanted = rtsEnv == "1" || (rtsEnv != "0" && PostFX.RayTracedShadows);
        if (rtShadowsWanted && EnsureRtShadows())
            DrawRtShadows(viewProj, lightDir);

        var ctx = new Dx12FrameContext
        {
            View = view, Proj = proj, ViewProj = viewProj,
            ProjUnjittered = projUnjittered, ViewProjUnjittered = viewProjUnjittered,
            PrevViewProjUnjittered = viewProjPrevForMotion,
            CurrentJitter = currentJitter, CamPos = camPos,
            LightDir = lightDir, LightColor = lightColor, Ambient = ambient, Exposure = exposure,
            WholeMeshRenderers = wholeMeshRenderers, FrustumPlanes = frustumPlanes,
            CascadeMatrices = cascadeMatrices,
            TargetW = targetW, TargetH = targetH, OutputW = outputW, OutputH = outputH,
            Dev = dev, Target = target, Ldr = ldr, GBuffer = gbuffer,
            Ibl = ibl, SkyLuts = skyLuts, ClusteredLights = clusteredLights,
            ShadowMap = shadowMap, GpuDriven = gpuDriven,
            Vsm = vsm, VsmActiveThisFrame = vsmActiveThisFrame,
            RtShadowMask = rtShadowMask,
            Dxr = dxr,
            DdgiGrid = ddgiPass.Grid,
            FrameCbAddress =
                frameCb.Gpu,
            Doors = doors, PostFX = PostFX, Stats = RenderStats.Scene,
            FrameCounter = DeterministicCapture ? 0 : frameCounter,
            BarriersDerived = barriersPath,
            DeterministicCapture = DeterministicCapture,
            AoResult = gtaoPass.ResultSrvCpu,
            AoToNonPixelShaderResource = () => gtaoPass.AoTarget.ColorToNonPixelShaderResource(),
            SkyOcclusionActive = rtaoPass.WillRun(doors, PostFX, dxr, dev),
            TaaActive = taaOn, FsrActive = fsrActive,
            Fsr = fsr, FsrOutput = fsrOutput, MotionPrevValid = motionPrevValid,
            ActiveUpscaler = activeUpscaler, Dlss = dlss, Xess = xess,
            SceneColor = fsrActive ? fsrOutput : target,
            IblActiveThisFrame = iblActiveThisFrame,
            ShadowsThisFrame = shadowsThisFrame,
            RtShadowsThisFrame = rtShadowsThisFrame,
        };

        ctx.GiActiveThisFrame = Dx12DdgiPass.WouldRun(ctx);

        ctx.GrainFrame = DeterministicCapture ? 0 : frameCounter;

        featureBridge.Apply();
        FP("geometry+deferred+setup");
        GpuMarkEnd();

        if (graphPath) graph.ExecuteGraph(ctx);
        else graph.Execute(ctx);
        FP("graph.Execute(all passes)");

        if (!PresentToScreen)
            ldr.ColorToShaderResource();

        dev.EndFrame();

        if (jitterOn) taaFrame++;
        frameCounter++;
        if (frameCounter >= 1 << 24) frameCounter = 0;
        motionPrevViewProj = viewProjUnjittered;
        motionPrevValid = true;

        RenderStats.Scene.DrawCalls = draws;
        RenderStats.Scene.Triangles = tris;
        RenderStats.Scene.SubMeshesCulled = culled;
        RenderStats.Scene.CpuFrameMs = cpuFrameSw.Elapsed.TotalMilliseconds;
        if (PassTimingEnabled)
        {
            double sum = 0;
            foreach (var p in RenderStats.Scene.GpuPasses) sum += p.Ms;
            RenderStats.Scene.GpuFrameMs = sum;
        }
        return new RenderMetrics(draws, 0, (int)tris, 0, 0f);
    }

    void GatherPunctualLights(Matrix4x4 view, Matrix4x4 proj)
    {
        clusteredLights.BeginGather();
        foreach (PointLight p in RuntimeSet<PointLight>.ReadOnlyCollection)
        {
            if (p is null || !p.IsActive) continue;
            clusteredLights.AddPoint(p.transform.WorldPosition, p.Range,
                p.PhysicalColor, p.SourceRadius);
        }

        foreach (SpotLight s in RuntimeSet<SpotLight>.ReadOnlyCollection)
        {
            if (s is null || !s.IsActive) continue;
            Vector3 dir = Vector3.Transform(Vector3.UnitZ, s.transform.WorldRotation);
            float inner = Math.Clamp(s.InnerAngle, 0f, 89f) * (MathF.PI / 180f);
            float outer = Math.Clamp(MathF.Max(s.OuterAngle, s.InnerAngle), 0f, 89.9f) * (MathF.PI / 180f);
            clusteredLights.AddSpot(s.transform.WorldPosition, dir, s.Range,
                s.PhysicalColor, MathF.Cos(inner), MathF.Cos(outer), s.SourceRadius);
        }

        foreach (RectLight rl in RuntimeSet<RectLight>.ReadOnlyCollection)
        {
            if (rl is null || !rl.IsActive) continue;
            Quaternion rot = rl.transform.WorldRotation;
            Vector3 fwd = Vector3.Transform(Vector3.UnitZ, rot);
            Vector3 right = Vector3.Transform(Vector3.UnitX, rot);
            clusteredLights.AddRect(rl.transform.WorldPosition, fwd, right,
                MathF.Max(rl.Width, 0.001f) * 0.5f, MathF.Max(rl.Height, 0.001f) * 0.5f,
                rl.Range, rl.PhysicalColor, rl.TwoSided);
        }

        clusteredLights.Cull(view, proj, targetW, targetH, CameraNear, CameraFar);
    }

    UpscaleMode ResolveUpscaleMode()
    {
        UpscaleMode Resolve(UpscaleMode m) => m == UpscaleMode.Auto ? AutoUpscaleModeForHardware() : m;
        string? env = fsrEnv;
        if (string.IsNullOrEmpty(env)) return Resolve(PostFX.UpscaleMode);
        return env.Trim().ToLowerInvariant() switch
        {
            "0" or "off" => UpscaleMode.Off,
            "1" or "native" or "nativeaa" => UpscaleMode.NativeAA,
            "quality" or "q" => UpscaleMode.Quality,
            "balanced" or "b" => UpscaleMode.Balanced,
            "performance" or "perf" or "p" => UpscaleMode.Performance,
            "ultra" or "ultraperformance" or "up" => UpscaleMode.UltraPerformance,
            "auto" or "a" => AutoUpscaleModeForHardware(),
            "dlss" or "dlssquality" => UpscaleMode.DlssQuality,
            "dlssbalanced" => UpscaleMode.DlssBalanced,
            "dlssperformance" or "dlssperf" => UpscaleMode.DlssPerformance,
            "dlssultra" or "dlssultraperformance" => UpscaleMode.DlssUltraPerformance,
            "xess" or "xessquality" => UpscaleMode.XessQuality,
            "xessbalanced" => UpscaleMode.XessBalanced,
            "xessperformance" or "xessperf" => UpscaleMode.XessPerformance,
            "xessultra" or "xessultraperformance" => UpscaleMode.XessUltraPerformance,
            _ => Resolve(PostFX.UpscaleMode),
        };
    }

    UpscaleMode AutoUpscaleModeForHardware()
    {
        ulong vramMB = dev.DedicatedVideoMemoryBytes / (1024 * 1024);
        if (vramMB == 0)     return UpscaleMode.Off;
        if (vramMB <  5000)  return UpscaleMode.Performance;
        if (vramMB <  9000)  return UpscaleMode.Balanced;
        if (vramMB < 13000)  return UpscaleMode.Quality;
        return UpscaleMode.Off;
    }

    unsafe bool EnsureRtShadows()
    {
        if (!dxr.CheckAvailable("RTShadows")) return false;
        if (rtShadowBuilt) return true;
        rtShadowBuilt = true;

        var device5 = dxr.Device5;

        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0),
            ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 3, baseShaderRegister: 0);
        var uavRange = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 0);
        var table = new RootParameter1(new RootDescriptorTable1(srvRange, uavRange), ShaderVisibility.All);
        rtShadowRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, table })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("DxrShadows.hlsl");
        byte[] dxil = Dx12ShaderCompiler.Compile(DxcShaderStage.Library, hlsl, "", "DxrShadows.hlsl");
        var subs = new[]
        {
            new StateSubObject(new DxilLibraryDescription(dxil,
                new ExportDescription("RayGen"), new ExportDescription("Miss"), new ExportDescription("ClosestHit"))),
            new StateSubObject(new HitGroupDescription("HitGroup", HitGroupType.Triangles, "", "ClosestHit", "")),
            new StateSubObject(new RaytracingShaderConfig(4, 8)), new StateSubObject(new RaytracingPipelineConfig(1)),
            new StateSubObject(new GlobalRootSignature(rtShadowRootSig)),
        };
        rtShadowPso = device5.CreateStateObject(new StateObjectDescription(StateObjectType.RaytracingPipeline, subs));

        using ID3D12StateObjectProperties props = rtShadowPso.QueryInterface<ID3D12StateObjectProperties>();
        uint idSize = Vortice.Direct3D12.D3D12.ShaderIdentifierSizeInBytes;
        rtShadowSbt = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(RtSbtSlot * 3), ResourceStates.GenericRead);
        byte* sp = rtShadowSbt.Map<byte>(0);
        System.Runtime.CompilerServices.Unsafe.CopyBlock(sp + 0 * RtSbtSlot, (void*)props.GetShaderIdentifier("RayGen"),
            idSize);
        System.Runtime.CompilerServices.Unsafe.CopyBlock(sp + 1 * RtSbtSlot, (void*)props.GetShaderIdentifier("Miss"),
            idSize);
        System.Runtime.CompilerServices.Unsafe.CopyBlock(sp + 2 * RtSbtSlot,
            (void*)props.GetShaderIdentifier("HitGroup"), idSize);
        rtShadowSbt.Unmap(0);

        rtShadowCb = new Dx12FrameCb<RtShadowConstants>(dev);
        rtShadowHeap = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 4, shaderVisible: true,
            framesInFlight: dev.FramesInFlight);

        var dnCbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0),
            ShaderVisibility.Pixel);
        var dnSrvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 3, baseShaderRegister: 0);
        var dnSrvTable = new RootParameter1(new RootDescriptorTable1(dnSrvRange), ShaderVisibility.Pixel);
        var dnSamp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        rtShadowDenoiseRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { dnCbv, dnSrvTable }, new[] { dnSamp })));

        string dnHlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("DxrShadowDenoise.hlsl");
        byte[] dnVs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, dnHlsl, "VSMain", "DxrShadowDenoise.hlsl");
        ID3D12PipelineState MakeDenoisePso(string entry) => dev.Device.CreateGraphicsPipelineState(
            new GraphicsPipelineStateDescription
            {
                RootSignature = rtShadowDenoiseRootSig, VertexShader = dnVs,
                PixelShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, dnHlsl, entry, "DxrShadowDenoise.hlsl"),
                InputLayout = null, PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
                RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
                DepthStencilState = DepthStencilDescription.None,
                RenderTargetFormats = new[] { Format.R8_UNorm }, DepthStencilFormat = Format.Unknown,
                SampleDescription = new SampleDescription(1, 0),
            });
        rtShadowDenoiseHPso = MakeDenoisePso("PSBlurH");
        rtShadowDenoiseVPso = MakeDenoisePso("PSBlurV");

        int dnCbSize = (System.Runtime.InteropServices.Marshal.SizeOf<RtShadowDenoiseConstants>() + 255) & ~255;
        rtShadowDenoiseCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)dnCbSize), ResourceStates.GenericRead);
        rtShadowDenoiseCbMapped = rtShadowDenoiseCb.Map<byte>(0);
        rtShadowDenoiseHeap = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 6, shaderVisible: true,
            framesInFlight: dev.FramesInFlight);

        AllocRtShadowMask();
        return true;
    }

    void AllocRtShadowMask()
    {
        rtShadowMask?.Dispose();
        rtShadowMask = new Dx12OffscreenTarget(dev, targetW, targetH, withDepth: false,
            colorFormat: Format.R8_UNorm, colorReadable: true, allowUav: true);
        if (rtShadowDenoiseRootSig != null)
        {
            rtShadowDenoiseScratch?.Dispose();
            rtShadowDenoiseScratch = new Dx12OffscreenTarget(dev, targetW, targetH, withDepth: false,
                colorFormat: Format.R8_UNorm, colorReadable: true, allowUav: false);
        }
    }

    unsafe void DrawRtShadows(Matrix4x4 viewProj, Vector3 lightDir)
    {
        var sceneAS = dxr.SceneAS;
        sceneAS.Ensure(RendererSet);
        if (!sceneAS.Valid) return;

        gbuffer.ToShaderResource();

        Matrix4x4.Invert(viewProj, out Matrix4x4 invVP);
        Vector3 sun = lightDir.LengthSquared() < 1e-8f ? Vector3.UnitY : Vector3.Normalize(lightDir);

        float angularDiamDeg = DirectionalLight.Instance?.AngularDiameter ?? 0.53f;
        float sunAngularRadius = angularDiamDeg * 0.5f * (MathF.PI / 180f);
        int rayCount = rtShadowHardForce ? 1 : RtShadowSoftRays;
        int frameIdx = DeterministicCapture ? 0 : frameCounter;
        rtShadowCb.Write(new RtShadowConstants
        {
            InvViewProj = Matrix4x4.Transpose(invVP), SunDir = sun, NormalBias = 0.05f,
            SunAngularRadius = sunAngularRadius, RayCount = rayCount, FrameIndex = frameIdx,
        });

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        sceneAS.CreateTlasSrv(rtShadowHeap.Cpu(0));
        dev.Device.CopyDescriptorsSimple(1, rtShadowHeap.Cpu(1), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, rtShadowHeap.Cpu(2), gbuffer.ColorSrvCpu(1), heapType);
        dev.Device.CreateUnorderedAccessView(rtShadowMask.RenderTarget, null, new UnorderedAccessViewDescription
        {
            Format = Format.R8_UNorm, ViewDimension = UnorderedAccessViewDimension.Texture2D,
        }, rtShadowHeap.Cpu(3));

        rtShadowMask.ColorToUnorderedAccess();
        uint idSize = Vortice.Direct3D12.D3D12.ShaderIdentifierSizeInBytes;
        dev.ExecuteSync(cl =>
        {
            cl.SetDescriptorHeaps(rtShadowHeap.Heap);
            cl.SetComputeRootSignature(rtShadowRootSig);
            cl.SetPipelineState1(rtShadowPso);
            cl.SetComputeRootConstantBufferView(0, rtShadowCb.Gpu);
            cl.SetComputeRootDescriptorTable(1, rtShadowHeap.Gpu(0));
            cl.DispatchRays(new DispatchRaysDescription
            {
                Width = (uint)targetW, Height = (uint)targetH, Depth = 1,
                RayGenerationShaderRecord = new GpuVirtualAddressRange
                    { StartAddress = rtShadowSbt.GPUVirtualAddress, SizeInBytes = idSize },
                MissShaderTable = new GpuVirtualAddressRangeAndStride
                {
                    StartAddress = rtShadowSbt.GPUVirtualAddress + RtSbtSlot, SizeInBytes = idSize,
                    StrideInBytes = idSize
                },
                HitGroupTable = new GpuVirtualAddressRangeAndStride
                {
                    StartAddress = rtShadowSbt.GPUVirtualAddress + 2 * RtSbtSlot, SizeInBytes = idSize,
                    StrideInBytes = idSize
                },
            });
        });
        rtShadowMask.ColorToShaderResource();

        if (rayCount > 1)
            DenoiseRtShadowMask();

        rtShadowsThisFrame = true;
    }

    unsafe void DenoiseRtShadowMask()
    {
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Vector2 texel = new(1f / targetW, 1f / targetH);

        void Pass(ID3D12PipelineState pso, Dx12OffscreenTarget src, Dx12OffscreenTarget dst, Vector2 dir,
            int srvSlot)
        {
            *(RtShadowDenoiseConstants*)rtShadowDenoiseCbMapped = new RtShadowDenoiseConstants
            {
                TexelSize = texel, DepthSigma = 0.05f, NormalSigma = 16f, Direction = dir,
            };
            src.ColorToShaderResource();
            dev.Device.CopyDescriptorsSimple(1, rtShadowDenoiseHeap.Cpu(srvSlot), src.ColorSrvCpu, heapType);
            dev.Device.CopyDescriptorsSimple(1, rtShadowDenoiseHeap.Cpu(srvSlot + 1), gbuffer.DepthSrvCpu, heapType);
            dev.Device.CopyDescriptorsSimple(1, rtShadowDenoiseHeap.Cpu(srvSlot + 2), gbuffer.ColorSrvCpu(1), heapType);
            dst.RenderColorOnly(cl =>
            {
                cl.SetGraphicsRootSignature(rtShadowDenoiseRootSig);
                cl.SetPipelineState(pso);
                cl.SetDescriptorHeaps(rtShadowDenoiseHeap.Heap);
                cl.SetGraphicsRootConstantBufferView(0, rtShadowDenoiseCb.GPUVirtualAddress);
                cl.SetGraphicsRootDescriptorTable(1, rtShadowDenoiseHeap.Gpu(srvSlot));
                cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                cl.DrawInstanced(3, 1, 0, 0);
            });
        }
        Pass(rtShadowDenoiseHPso, rtShadowMask, rtShadowDenoiseScratch, new Vector2(1f, 0f), 0);
        Pass(rtShadowDenoiseVPso, rtShadowDenoiseScratch, rtShadowMask, new Vector2(0f, 1f), 3);
        rtShadowMask.ColorToShaderResource();
    }

    int ComputeShadowCasterStamp()
    {
        var h = new System.HashCode();
        foreach (IStaticMeshRenderer r in RendererSet)
        {
            if (r is null || !r.IsActive || !r.IsRenderable) continue;
            GLMatrix4 m = r.Transform.RenderMatrix;
            h.Add(m.M11);
            h.Add(m.M12);
            h.Add(m.M13);
            h.Add(m.M14);
            h.Add(m.M21);
            h.Add(m.M22);
            h.Add(m.M23);
            h.Add(m.M24);
            h.Add(m.M31);
            h.Add(m.M32);
            h.Add(m.M33);
            h.Add(m.M34);
            h.Add(m.M41);
            h.Add(m.M42);
            h.Add(m.M43);
            h.Add(m.M44);
            h.Add(r.SubMeshIndex);
        }

        return h.ToHashCode();
    }

    static int SnapPow2(int v)
    {
        if (v <= 1) return 1;
        int p = 1;
        while (p < v) p <<= 1;
        return (p - v) < (v - (p >> 1)) ? p : (p >> 1);
    }

    unsafe void RenderShadows(Matrix4x4 camView, Matrix4x4 camProj, LightUniforms light)
    {
        shadowsThisFrame = false;
        if (DirectionalLight.Instance is null) return;

        Vector3 sunTravel = -light.Direction;
        if (sunTravel.LengthSquared() < 1e-8f) return;

        bool volumesDriving = doors.Volumes;
        activeCascadeCount = volumesDriving ? Math.Clamp(PostFX.ShadowCascadeCount, 1, MaxCascades) : MaxCascades;
        float shadowDistance = volumesDriving && PostFX.ShadowMaxDistance > 0f
            ? PostFX.ShadowMaxDistance : DirectionalLight.Instance.ShadowDistance;
        float splitLambda = volumesDriving ? Math.Clamp(PostFX.ShadowSplitDistribution, 0f, 1f) : 0.7f;

        int wantSize = volumesDriving ? Math.Clamp(SnapPow2(PostFX.ShadowResolution), 512, 4096) : 2048;
        if (wantSize != shadowMapSize)
        {
            shadowMapSize = wantSize;
            shadowMap.Dispose();
            shadowMap = new Dx12ShadowMap(dev, shadowMapSize, MaxCascades);
            shadowMapEverRendered = false;
        }

        Dx12ShadowMath.ComputeCascades(camView, camProj, sunTravel, shadowDistance, shadowMapSize,
            cascadeMatrices, cascadeDepthRanges, splitLambda, activeCascadeCount);

        int casterStamp = ComputeShadowCasterStamp();
        bool cascadesUnchanged = shadowMapEverRendered && casterStamp == lastCasterStamp
            && activeCascadeCount == lastActiveCascadeCount;
        for (int c = 0; cascadesUnchanged && c < activeCascadeCount; c++)
            cascadesUnchanged &= cascadeMatrices[c].Equals(lastCascadeMatrices[c]);
        if (shadowCacheOn && cascadesUnchanged)
        {
            shadowsThisFrame = true;
            if (shadowCacheDebugOn)
                Console.WriteLine("[ShadowCache] cascades unchanged — skipped re-render.");
            return;
        }

        lastCasterStamp = casterStamp;
        lastActiveCascadeCount = activeCascadeCount;
        for (int c = 0; c < activeCascadeCount; c++) lastCascadeMatrices[c] = cascadeMatrices[c];
        shadowMapEverRendered = true;

        int slotBase = dev.FrameSlot * shadowCbSlotCount;
        int slotEnd = slotBase + shadowCbSlotCount;
        int slot = slotBase;
        var fills = shadowFills;
        fills.Clear();
        for (int c = 0; c < activeCascadeCount; c++)
        {
            ExtractFrustumPlanes(cascadeMatrices[c]);
            foreach (IStaticMeshRenderer r in RendererSet)
            {
                if (r is null || !r.IsActive || !r.IsRenderable) continue;
                if (gpuDrivenOn && r.SubMeshIndex < 0) continue;
                Mesh mesh = r.SharedMesh;
                if (mesh is null) continue;
                if (mesh.VertexBuffer is not Dx12Buffer<GLVector3> vb || vb.Resource is null) continue;
                if (mesh.IndexBuffer is not Dx12IndexBuffer ib || ib.Resource is null) continue;
                Matrix4x4 model = r.Transform.RenderMatrix;
                Matrix4x4 lightMvp = model * cascadeMatrices[c];
                int only = r.SubMeshIndex;
                int first = only >= 0 ? only : 0;
                int last = only >= 0 ? only : mesh.SubMeshes.Length - 1;
                for (int s = first; s <= last; s++)
                {
                    if ((uint)s >= (uint)mesh.SubMeshes.Length) break;
                    SubMeshData sub = mesh.SubMeshes[s];
                    if (sub.IndexCount <= 0) continue;
                    mesh.GetSubMeshBounds(s, out GLVector3 lmin, out GLVector3 lmax);
                    if (!AabbInFrustum(lmin, lmax, model)) continue;
                    if (slot >= slotEnd) break;
                    *(ShadowConstants*)(shadowCbMapped + (long)slot * shadowCbSlotSize) =
                        new ShadowConstants { LightMvp = Matrix4x4.Transpose(lightMvp) };
                    fills.Add((c, vb, ib, sub.IndexStart, sub.IndexCount, slot));
                    slot++;
                }
            }
        }

        bool gpuShadows = gpuDrivenOn && wholeMeshRenderers.Count > 0;
        if (fills.Count == 0 && !gpuShadows) return;

        Action<ID3D12GraphicsCommandList4> recordShadows = cl =>
        {
            if (gpuShadows) gpuDriven.BuildShadowCull(cl, wholeMeshRenderers, cascadeMatrices, activeCascadeCount);
            shadowMap.ToDepthWrite(cl);
            for (int c = 0; c < activeCascadeCount; c++)
            {
                shadowMap.RenderCascade(cl, c, cc =>
                {
                    cc.SetGraphicsRootSignature(shadowRootSig);
                    cc.SetPipelineState(shadowPso);
                    cc.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                    foreach (var f in fills)
                    {
                        if (f.cascade != c) continue;
                        cc.SetGraphicsRootConstantBufferView(0,
                            shadowCb.GPUVirtualAddress + (ulong)((long)f.cbSlot * shadowCbSlotSize));
                        cc.IASetVertexBuffers(0,
                            new VertexBufferView(f.vb.GpuAddress, (uint)f.vb.ByteSize, (uint)f.vb.Stride));
                        cc.IASetIndexBuffer(new IndexBufferView(f.ib.GpuAddress, (uint)f.ib.ByteSize, Format.R32_UInt));
                        cc.DrawIndexedInstanced((uint)f.count, 1, (uint)f.start, 0, 0);
                    }

                    if (gpuShadows) gpuDriven.DrawShadowCascade(cc, c);
                });
            }

            shadowMap.ToShaderResource(cl);
        };
        if (Pp1InlineSyncs) dev.ExecuteSync(recordShadows);
        else                dev.ExecuteUpload(recordShadows);
        shadowsThisFrame = true;
    }

    unsafe void RenderVsm(Vector3 camPos, LightUniforms light)
    {
        shadowsThisFrame = false;
        if (DirectionalLight.Instance is null) return;
        Vector3 sunTravel = -light.Direction;
        if (sunTravel.LengthSquared() < 1e-8f) return;

        bool volumesDriving = doors.Volumes;
        int wantRes = volumesDriving ? Math.Clamp(SnapPow2(PostFX.VsmResolution), 512, 4096) : 2048;
        int wantLevels = volumesDriving ? Math.Clamp(PostFX.VsmClipmapLevels, 1, Dx12VirtualShadowMap.MaxLevels) : 12;
        float wantExtent = volumesDriving && PostFX.VsmLevel0Extent > 0f ? PostFX.VsmLevel0Extent : 4f;
        if (vsm == null || vsm.Resolution != wantRes || vsm.Levels != wantLevels
            || MathF.Abs(vsm.Level0Extent - wantExtent) > 1e-4f)
        {
            vsm?.Dispose();
            vsm = new Dx12VirtualShadowMap(dev, wantRes, wantLevels, wantExtent);
        }

        int casterStamp = ComputeShadowCasterStamp();
        vsm.Fit(camPos, sunTravel, casterStamp, shadowCacheOn);

        bool anyDirty = false;
        for (int i = 0; i < vsm.Levels; i++) anyDirty |= vsm.LevelDirty[i];
        if (!anyDirty)
        {
            shadowsThisFrame = true;
            return;
        }

        int slotBase = dev.FrameSlot * shadowCbSlotCount;
        int slotEnd = slotBase + shadowCbSlotCount;
        int slot = slotBase;
        var fills = shadowFills;
        fills.Clear();
        for (int c = 0; c < vsm.Levels; c++)
        {
            if (!vsm.LevelDirty[c]) continue;
            ExtractFrustumPlanes(vsm.LightMatrices[c]);
            foreach (IStaticMeshRenderer r in RendererSet)
            {
                if (r is null || !r.IsActive || !r.IsRenderable) continue;
                Mesh mesh = r.SharedMesh;
                if (mesh is null) continue;
                if (mesh.VertexBuffer is not Dx12Buffer<GLVector3> vb || vb.Resource is null) continue;
                if (mesh.IndexBuffer is not Dx12IndexBuffer ib || ib.Resource is null) continue;
                Matrix4x4 model = r.Transform.RenderMatrix;
                Matrix4x4 lightMvp = model * vsm.LightMatrices[c];
                int only = r.SubMeshIndex;
                int first = only >= 0 ? only : 0;
                int last = only >= 0 ? only : mesh.SubMeshes.Length - 1;
                for (int s = first; s <= last; s++)
                {
                    if ((uint)s >= (uint)mesh.SubMeshes.Length) break;
                    SubMeshData sub = mesh.SubMeshes[s];
                    if (sub.IndexCount <= 0) continue;
                    mesh.GetSubMeshBounds(s, out GLVector3 lmin, out GLVector3 lmax);
                    if (!AabbInFrustum(lmin, lmax, model)) continue;
                    if (slot >= slotEnd) break;
                    *(ShadowConstants*)(shadowCbMapped + (long)slot * shadowCbSlotSize) =
                        new ShadowConstants { LightMvp = Matrix4x4.Transpose(lightMvp) };
                    fills.Add((c, vb, ib, sub.IndexStart, sub.IndexCount, slot));
                    slot++;
                }
            }
            vsm.MarkRendered(c);
        }

        if (fills.Count == 0) { shadowsThisFrame = true; return; }

        Action<ID3D12GraphicsCommandList4> recordVsm = cl =>
        {
            vsm.ToDepthWrite(cl);
            for (int c = 0; c < vsm.Levels; c++)
            {
                if (!vsm.LevelDirty[c]) continue;
                vsm.RenderLevel(cl, c, clear: true, cc =>
                {
                    cc.SetGraphicsRootSignature(shadowRootSig);
                    cc.SetPipelineState(shadowPso);
                    cc.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                    foreach (var f in fills)
                    {
                        if (f.cascade != c) continue;
                        cc.SetGraphicsRootConstantBufferView(0,
                            shadowCb.GPUVirtualAddress + (ulong)((long)f.cbSlot * shadowCbSlotSize));
                        cc.IASetVertexBuffers(0,
                            new VertexBufferView(f.vb.GpuAddress, (uint)f.vb.ByteSize, (uint)f.vb.Stride));
                        cc.IASetIndexBuffer(new IndexBufferView(f.ib.GpuAddress, (uint)f.ib.ByteSize, Format.R32_UInt));
                        cc.DrawIndexedInstanced((uint)f.count, 1, (uint)f.start, 0, 0);
                    }
                });
            }
            vsm.ToShaderResource(cl);
        };
        if (Pp1InlineSyncs) dev.ExecuteSync(recordVsm);
        else                dev.ExecuteUpload(recordVsm);
        shadowsThisFrame = true;
    }


    unsafe bool DrawSkinnedBindless(ID3D12GraphicsCommandList4 cl, IStaticMeshRenderer r, Mesh mesh,
        Dx12GpuDrivenRenderer.SkinnedBuffers skb, Dx12Buffer<Vector2> ub, Dx12IndexBuffer ib,
        Matrix4x4 model, Matrix4x4 mvp, ref int slot, ref int draws, ref long tris, ref int cpuDrawIndex)
    {
        Span<VertexBufferView> v = stackalloc VertexBufferView[4];
        int vcount = skb.VertexCount;
        v[0] = new VertexBufferView(skb.Pos.GPUVirtualAddress, (uint)(vcount * 12), 12);
        v[1] = new VertexBufferView(skb.Normal.GPUVirtualAddress, (uint)(vcount * 12), 12);
        v[2] = new VertexBufferView(ub.GpuAddress, (uint)ub.ByteSize, (uint)ub.Stride);
        v[3] = new VertexBufferView(skb.Tangent.GPUVirtualAddress, (uint)(vcount * 16), 16);
        cl.IASetVertexBuffers(0, v);
        cl.IASetIndexBuffer(new IndexBufferView(ib.GpuAddress, (uint)ib.ByteSize, Format.R32_UInt));

        int only = r.SubMeshIndex;
        int first = only >= 0 ? only : 0;
        int last = only >= 0 ? only : mesh.SubMeshes.Length - 1;
        for (int s = first; s <= last; s++)
        {
            if ((uint)s >= (uint)mesh.SubMeshes.Length) break;
            SubMeshData sub = mesh.SubMeshes[s];
            if (sub.IndexCount <= 0) continue;
            Material mat = r.MaterialFor(s);
            if (mat is null || mat.Transparent) continue;
            int mid = gpuDriven.TryMaterialId(mat, out int rid) ? rid : gpuDriven.ResolveOrRegisterMaterialId(mat);
            if (mid < 0) return false;
            if (!gpuDriven.CpuBindlessWrite(cpuDrawIndex, mvp, model, mid)) return false;
            cl.SetGraphicsRoot32BitConstant(0, (uint)cpuDrawIndex, 0);
            cl.DrawIndexedInstanced((uint)sub.IndexCount, 1, (uint)sub.IndexStart, 0, 0);
            cpuDrawIndex++;
            draws++; tris += sub.IndexCount / 3;
            slot++;
        }
        return true;
    }

    void BindSrv(int visibleSlot, Texture2D tex, TextureType type, Dx12Texture2D explicitFallback)
        => BindSrvInto(srvVisible, visibleSlot, tex, type, explicitFallback);

    void BindSrvInto(Dx12DescriptorHeap heap, int slot, Texture2D tex, TextureType type, Dx12Texture2D explicitFallback)
    {
        var dx = (tex as Dx12Texture2D)
                 ?? explicitFallback
                 ?? (DefaultTextures.Neutral(type) as Dx12Texture2D);
        dev.Device.CopyDescriptorsSimple(1, heap.Cpu(slot), dx.SrvCpu,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
    }

    public override void PostRenderCleanUp()
    {
        foreach (IStaticMeshRenderer r in RendererSet)
            if (r != null)
                r.RenderedThisFrame = false;
    }

    public void SaveFrame(string path) => ldr?.SaveBmp(path);

    public object DumpGBuffer(string dir)
    {
        if (gbuffer == null) return new { ok = false, error = "no g-buffer (renderer not initialized)" };
        System.IO.Directory.CreateDirectory(dir);
        int w = gbuffer.Width, h = gbuffer.Height;

        byte[] depth = gbuffer.ReadbackRaw(-1, out int depthBpp);
        byte[] normal = gbuffer.ReadbackRaw(1, out int normalBpp);
        byte[] albedo = gbuffer.ReadbackRaw(0, out int albedoBpp);

        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "depth.bin"), depth);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "normal.bin"), normal);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "albedo.bin"), albedo);

        return new
        {
            ok = true, width = w, height = h,
            buffers = new object[]
            {
                new
                {
                    name = "depth", file = "depth.bin", format = "R32_Float", bytesPerPixel = depthBpp,
                    encoding = "window depth [0,1]; world pos = unproject(uv, depth) via InvViewProj"
                },
                new
                {
                    name = "normal", file = "normal.bin", format = Dx12GBuffer.ColorFormats[1].ToString(), bytesPerPixel = normalBpp,
                    encoding = "world normal PACKED as N*0.5+0.5 in RGB; unpack N = rgb*2-1 (RGB10A2 when GBUFFER_PACK, else RGBA16F)"
                },
                new
                {
                    name = "albedo", file = "albedo.bin", format = "R8G8B8A8_UNorm_sRGB", bytesPerPixel = albedoBpp,
                    encoding = "albedo.rgb sRGB; a = specular F0"
                },
            },
        };
    }

    public object DumpHdrColor(string file)
    {
        if (target == null) return new { ok = false, error = "no HDR target (renderer not initialized)" };
        int w = target.Width, h = target.Height;
        var rgb = new float[w * h * 3];
        target.ReadColorRgb(rgb);
        var bytes = new byte[rgb.Length * 4];
        Buffer.BlockCopy(rgb, 0, bytes, 0, bytes.Length);
        System.IO.File.WriteAllBytes(file, bytes);
        return new
        {
            ok = true, width = w, height = h, channels = 3,
            format = "R32_Float (little-endian), 3 floats/pixel (RGB), top-down rows",
            file,
        };
    }

    public int Width => outputW;
    public int Height => outputH;

    public override void RenderOpaque(IReadOnlyCollection<IStaticMeshRenderer> renderTargets,
        RendererArgs args, bool isShadowPass)
    {
    }

    public override void RenderSkybox(IReadOnlyCollection<ISkyboxDrawable> renderTargets, RendererArgs args)
    {
    }

    public override void RenderInstancing(BatchGroup<IStaticMeshRenderer> batchGroup, RendererArgs args)
    {
    }

    public override void RenderInstancing(Mesh mesh, Material material, GLMatrix4[] transforms, RendererArgs args)
    {
        if (mesh is null || material is null || transforms is null || transforms.Length == 0) return;
        var copy = (Matrix4x4[])transforms.Clone();
        pendingInstanced.Add((mesh, material, copy));
    }

    void FlushInstanced(ID3D12GraphicsCommandList4 cl, Matrix4x4 viewProj, Matrix4x4 viewProjUnjittered,
                        Matrix4x4 view)
    {
        if (instanceCuller is null || pendingInstanced.Count == 0) { pendingInstanced.Clear(); return; }
        instanceCuller.BeginFrame();
        instanceCuller.SetHizDims(gpuDriven.HizWidth, gpuDriven.HizHeight, gpuDriven.HizMipCount);
        foreach (var (mesh, mat, transforms) in pendingInstanced)
        {
            if (mat.Transparent) continue;
            int matId = gpuDriven.ResolveOrRegisterMaterialId(mat);
            if (matId < 0) continue;
            instanceCuller.RenderInstanced(cl, mesh, -1, matId, transforms, viewProj, frustumPlanes,
                viewProjUnjittered, view, CameraNear, CameraFar, motionCb.Gpu, gpuDriven.MaterialsGpuAddress,
                gpuDriven.HizBindlessIndex, gpuDriven.HizOn);
        }
        pendingInstanced.Clear();
    }

    bool instTestQueried;
    Mesh instTestMesh; Material instTestMat;

    void InstanceCullSelfTest(Matrix4x4 viewProj, Matrix4x4 viewProjUnjittered, Matrix4x4 view)
    {
        if (Environment.GetEnvironmentVariable("BALLISTIC_DX12_INSTANCE_CULL_TEST") != "1") return;
        if (!instTestQueried)
        {
            instTestQueried = true;
            foreach (IStaticMeshRenderer r in RendererSet)
            {
                if (r is null || !r.IsActive || !r.IsRenderable || r.IsSkinned) continue;
                Mesh m = r.SharedMesh; if (m is null || m.SubMeshes.Length == 0) continue;
                Material mat = r.MaterialFor(0); if (mat is null || mat.Transparent) continue;
                instTestMesh = m; instTestMat = mat; break;
            }
        }
        if (instTestMesh is null) return;
        const int grid = 4; const float spacing = 2.0f;
        var xforms = new Matrix4x4[grid * grid];
        int k = 0;
        for (int z = 0; z < grid; z++)
            for (int x = 0; x < grid; x++)
                xforms[k++] = Matrix4x4.CreateTranslation((x - grid / 2f) * spacing, 0, (z - grid / 2f) * spacing);
        pendingInstanced.Add((instTestMesh, instTestMat, xforms));
    }

    readonly Vector4[] frustumPlanes = new Vector4[6];

    void ExtractFrustumPlanes(Matrix4x4 m)
    {
        Vector4 r1 = new(m.M11, m.M21, m.M31, m.M41);
        Vector4 r2 = new(m.M12, m.M22, m.M32, m.M42);
        Vector4 r3 = new(m.M13, m.M23, m.M33, m.M43);
        Vector4 r4 = new(m.M14, m.M24, m.M34, m.M44);
        frustumPlanes[0] = r4 + r1;
        frustumPlanes[1] = r4 - r1;
        frustumPlanes[2] = r4 + r2;
        frustumPlanes[3] = r4 - r2;
        frustumPlanes[4] = r3;
        frustumPlanes[5] = r4 - r3;
        for (int i = 0; i < 6; i++)
        {
            Vector3 n = new(frustumPlanes[i].X, frustumPlanes[i].Y, frustumPlanes[i].Z);
            float len = n.Length();
            if (len > 1e-6f) frustumPlanes[i] /= len;
        }
    }

    bool AabbInFrustum(GLVector3 localMin, GLVector3 localMax, Matrix4x4 model)
    {
        Vector3 wlo = new(float.MaxValue), whi = new(float.MinValue);
        for (int c = 0; c < 8; c++)
        {
            var lc = new Vector3((c & 1) == 0 ? localMin.X : localMax.X,
                (c & 2) == 0 ? localMin.Y : localMax.Y,
                (c & 4) == 0 ? localMin.Z : localMax.Z);
            Vector3 w = Vector3.Transform(lc, model);
            wlo = Vector3.Min(wlo, w);
            whi = Vector3.Max(whi, w);
        }

        for (int i = 0; i < 6; i++)
        {
            Vector4 p = frustumPlanes[i];
            Vector3 pv = new(p.X >= 0 ? whi.X : wlo.X, p.Y >= 0 ? whi.Y : wlo.Y, p.Z >= 0 ? whi.Z : wlo.Z);
            if (p.X * pv.X + p.Y * pv.Y + p.Z * pv.Z + p.W < 0f) return false;
        }

        return true;
    }
}
