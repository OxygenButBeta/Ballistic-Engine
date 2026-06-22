using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

public sealed class Dx12CompositePass : IRenderPass, IDisposable {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.Composite;
    public string Name => "Composite";

    // FAZ -1c — when render-graph v2 owns composite (BALLISTIC_DX12_RG=1) the v1 graph SKIPS this
    // pass; v2 drives Record() itself. Door off (default) => RgV2OwnsComposite is false => unchanged.
    public bool Enabled(Dx12FrameContext ctx) => !ctx.RgV2OwnsComposite;

    // FAZ -1c — render-graph v2 entry point: v2 imports SceneColor/Ldr + declares the access, then
    // calls this to run the SAME composite record body (byte-identical output to the v1 path).
    // The v1 graph normally derives the SceneColor->PixelShaderResource transition for composite when
    // ctx.BarriersDerived is on (the body then skips its own). Under v2 the v1 deriver is bypassed
    // (the pass is skipped in v1) AND v2 emits no barrier for the imports (by design — equal states),
    // so the body MUST own that transition. Force it here so Record() never reads SceneColor in the
    // wrong state regardless of ctx.BarriersDerived.
    public void RecordV2(Dx12FrameContext ctx) {
        ctx.SceneColor.ColorToShaderResource();
        Record(ctx);
    }

    public void Declare(Dx12PassBuilder b) {
        b.Read(b.Resource("SceneColor"));
        b.Read(b.Resource("FsrOutput"));
        b.Read(b.Resource("Ssao"));
        b.Write(b.Resource("Ldr"));
        b.DeriveBarriers();
        b.Use(Dx12ResourceUsage.SceneColorShaderRead);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CompositeConstants {
        public float ExposureMul; public float BloomIntensity; public float AutoExposure; public float LegacyMul;
        public float Compensation; public float PadAo; public float Tonemap; public float Contrast;
        public float Saturation; public float Sharpen; public float VignetteStrength; public float VignetteRoundness;
        public float ChromaticAberration; public float LensDistortion; public float FilmGrain; public float GrainTime;
        public Vector3 VignetteColor; public float Pad3;
        public Vector2 ScreenSize; public Vector2 Pad4;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct LumConstants {
        public float LimitMin; public float LimitMax; public float Calibrated; public float DeltaTime;
        public float SpeedDarkToLight; public float SpeedLightToDark; public float Reset; public float Pad;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct BloomConstants { public Vector2 TexelSize; public float Threshold; public float Knee; }

    [StructLayout(LayoutKind.Sequential)]
    struct HistConstants {
        public uint SrcWidth; public uint SrcHeight; public float MinLogLum; public float InvLogLumRange;
        public float MeteringMode; public float LuxMeterAnchor; public float LimitMin; public float LimitMax;
        public float FilterMin; public float FilterMax; public float DeltaTime; public float SpeedDarkToLight;
        public float SpeedLightToDark; public float Reset; public float Pad0; public float Pad1;
    }

    readonly Dx12Device dev;

    ID3D12RootSignature compositeRootSig;
    ID3D12PipelineState compositePso;
    Dx12FrameCb<CompositeConstants> compositeCb;
    Dx12DescriptorHeap compositeSrvVisible;

    ID3D12RootSignature lumRootSig;

    ID3D12PipelineState lumPso;

    Dx12OffscreenTarget lumTarget, lumHistory;
    bool lumHistoryValid;
    bool exposureDebugDumped;

    readonly bool manualExposureSet;
    readonly float manualExposureValue;
    readonly bool forceAutoExp;
    readonly bool forceHistogramExp;
    readonly bool exposureCalibrated;
    readonly bool exposureEmaOn;
    readonly bool acesTonemapEnv;
    readonly bool gradeDemoEnv;
    Dx12DescriptorHeap lumSrvVisible;
    Dx12FrameCb<LumConstants> lumCb;
    int emaDebugFrame;

    const int HistogramBins = 256;
    ID3D12RootSignature histClearRootSig, histBuildRootSig, histResolveRootSig;
    ID3D12PipelineState histClearPso, histBuildPso, histResolvePso;
    ID3D12Resource histogramBuffer;
    Dx12FrameCb<HistConstants> histCb;
    Dx12DescriptorHeap histClearHeap;
    Dx12DescriptorHeap histBuildHeap;

    Dx12DescriptorHeap histResolveHeap;

    ID3D12Resource histEvA, histEvB;
    CpuDescriptorHandle histEvASrv, histEvAUav, histEvBSrv, histEvBUav;
    ResourceStates histEvAState, histEvBState;
    bool histBuilt;
    bool histHistoryValid;

    const int BloomMaxLevels = 6;
    ID3D12RootSignature bloomRootSig;
    ID3D12PipelineState bloomDownThresholdPso, bloomDownPso, bloomUpPso;
    readonly Dx12OffscreenTarget[] bloomLevels = new Dx12OffscreenTarget[BloomMaxLevels];
    int bloomLevelCount;
    ID3D12Resource bloomCb;
    unsafe byte* bloomCbMapped;
    int bloomCbStride;
    int bloomCbSlots;
    long bloomCbFrameStride;
    long BloomCbFrameOffset => (long)dev.FrameSlot * bloomCbFrameStride;
    Dx12DescriptorHeap bloomSrvVisible;

    public unsafe Dx12CompositePass(Dx12Device device, int width, int height) {
        dev = device;
        manualExposureSet  = float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_EXPOSURE"),
                                 System.Globalization.CultureInfo.InvariantCulture, out manualExposureValue);
        forceAutoExp       = Environment.GetEnvironmentVariable("BALLISTIC_DX12_AUTOEXP") == "1";
        forceHistogramExp  = Environment.GetEnvironmentVariable("BALLISTIC_DX12_EXPOSURE_HISTOGRAM") == "1";
        exposureCalibrated = Environment.GetEnvironmentVariable("BALLISTIC_DX12_EXPOSURE_CALIB") != "0";
        exposureEmaOn      = Environment.GetEnvironmentVariable("BALLISTIC_DX12_EXPOSURE_EMA") != "0";
        acesTonemapEnv     = Environment.GetEnvironmentVariable("BALLISTIC_DX12_TONEMAP") == "aces";
        gradeDemoEnv       = Environment.GetEnvironmentVariable("BALLISTIC_DX12_GRADE_DEMO") == "1";
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 3, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var samp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        compositeRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { samp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("Composite.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "Composite.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "Composite.hlsl");
        compositePso = dev.CreateGraphicsPso(new GraphicsPipelineStateDescription {
            RootSignature = compositeRootSig, VertexShader = vs, PixelShader = ps, InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.ColorFormat },
            DepthStencilFormat = Format.Unknown, SampleDescription = new SampleDescription(1, 0),
        }, "Composite");

        compositeCb = new Dx12FrameCb<CompositeConstants>(dev);
        compositeSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 3, shaderVisible: true, framesInFlight: dev.FramesInFlight);

        BuildLumAverage();
        BuildBloom(width, height);
    }

    unsafe void BuildLumAverage() {
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.Pixel);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 2, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var samp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        lumRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { samp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("LumAverage.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "LumAverage.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "LumAverage.hlsl");
        lumPso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = lumRootSig, VertexShader = vs, PixelShader = ps, InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = new[] { Format.R16_Float }, DepthStencilFormat = Format.Unknown,
            SampleDescription = new SampleDescription(1, 0),
        });

        lumTarget = new Dx12OffscreenTarget(dev, 1, 1, withDepth: false,
            colorFormat: Format.R16_Float, colorReadable: true);
        lumHistory = new Dx12OffscreenTarget(dev, 1, 1, withDepth: false, colorFormat: Format.R16_Float, colorReadable: true);
        lumSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 2, shaderVisible: true, framesInFlight: dev.FramesInFlight);

        lumCb = new Dx12FrameCb<LumConstants>(dev);
    }

    unsafe void BuildHistogram() {
        if (histBuilt) return;
        var samp = new StaticSamplerDescription(ShaderVisibility.All, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        var cbvB0 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var uavHist = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 0);
        histClearRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] {
                cbvB0, new RootParameter1(new RootDescriptorTable1(uavHist), ShaderVisibility.All) })));
        var buildUav = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 0, registerSpace: 0, offsetInDescriptorsFromTableStart: 0);
        var buildSrv = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 0, registerSpace: 0, offsetInDescriptorsFromTableStart: 1);
        histBuildRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] {
                cbvB0, new RootParameter1(new RootDescriptorTable1(buildUav, buildSrv), ShaderVisibility.All) },
                new[] { samp })));
        var resHistUav = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 0, registerSpace: 0, offsetInDescriptorsFromTableStart: 0);
        var resPrevSrv = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 1, registerSpace: 0, offsetInDescriptorsFromTableStart: 1);
        var resEvUav   = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 1, registerSpace: 0, offsetInDescriptorsFromTableStart: 2);
        histResolveRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] {
                cbvB0, new RootParameter1(new RootDescriptorTable1(resHistUav, resPrevSrv, resEvUav), ShaderVisibility.All) },
                new[] { samp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("ExposureHistogram.hlsl");
        histClearPso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription {
            RootSignature = histClearRootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSClear", "ExposureHistogram.hlsl"),
        });
        histBuildPso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription {
            RootSignature = histBuildRootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSBuild", "ExposureHistogram.hlsl"),
        });
        histResolvePso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription {
            RootSignature = histResolveRootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSResolve", "ExposureHistogram.hlsl"),
        });

        histogramBuffer = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(HistogramBins * 4), ResourceFlags.AllowUnorderedAccess),
            ResourceStates.UnorderedAccess);
        histogramBuffer.Name = "ExposureHistogram";

        histCb = new Dx12FrameCb<HistConstants>(dev);
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        histClearHeap   = new Dx12DescriptorHeap(dev, heapType, 1, shaderVisible: true, framesInFlight: dev.FramesInFlight);
        histBuildHeap   = new Dx12DescriptorHeap(dev, heapType, 2, shaderVisible: true, framesInFlight: dev.FramesInFlight);
        histResolveHeap = new Dx12DescriptorHeap(dev, heapType, 3, shaderVisible: true, framesInFlight: dev.FramesInFlight);
        for (int slab = 0; slab < dev.FramesInFlight; slab++) {
            CreateHistUav(histClearHeap.CpuPhysical(slab * 1 + 0));
            CreateHistUav(histBuildHeap.CpuPhysical(slab * 2 + 0));
            CreateHistUav(histResolveHeap.CpuPhysical(slab * 3 + 0));
        }

        (histEvA, histEvASrv, histEvAUav) = MakeEvTarget();
        (histEvB, histEvBSrv, histEvBUav) = MakeEvTarget();
        histEvAState = histEvBState = ResourceStates.UnorderedAccess;
        histBuilt = true;
    }

    (ID3D12Resource res, CpuDescriptorHandle srv, CpuDescriptorHandle uav) MakeEvTarget() {
        var desc = ResourceDescription.Texture2D(Format.R32_Float, 1, 1, mipLevels: 1, arraySize: 1);
        desc.Flags = ResourceFlags.AllowUnorderedAccess;
        ID3D12Resource res = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            desc, ResourceStates.UnorderedAccess);
        res.Name = "ExposureAdaptedEv";
        int srvIdx = Dx12Backend.SrvStore.Allocate();
        int uavIdx = Dx12Backend.SrvStore.Allocate();
        CpuDescriptorHandle srv = Dx12Backend.SrvStore.Cpu(srvIdx);
        CpuDescriptorHandle uav = Dx12Backend.SrvStore.Cpu(uavIdx);
        dev.Device.CreateShaderResourceView(res, new ShaderResourceViewDescription {
            Format = Format.R32_Float, ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        }, srv);
        dev.Device.CreateUnorderedAccessView(res, null, new UnorderedAccessViewDescription {
            Format = Format.R32_Float, ViewDimension = UnorderedAccessViewDimension.Texture2D,
            Texture2D = new Texture2DUnorderedAccessView { MipSlice = 0 },
        }, uav);
        return (res, srv, uav);
    }

    unsafe void BuildBloom(int width, int height) {
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var samp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        bloomRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { samp })));

        var additive = new BlendDescription(Blend.One, Blend.One);

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("Bloom.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "Bloom.hlsl");
        ID3D12PipelineState MakePso(string entry, BlendDescription blend) => dev.Device.CreateGraphicsPipelineState(
            new GraphicsPipelineStateDescription {
                RootSignature = bloomRootSig, VertexShader = vs,
                PixelShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, entry, "Bloom.hlsl"),
                InputLayout = null, PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
                RasterizerState = RasterizerDescription.CullNone, BlendState = blend,
                DepthStencilState = DepthStencilDescription.None,
                RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat }, DepthStencilFormat = Format.Unknown,
                SampleDescription = new SampleDescription(1, 0),
            });
        bloomDownThresholdPso = MakePso("PSDownThreshold", BlendDescription.Opaque);
        bloomDownPso = MakePso("PSDown", BlendDescription.Opaque);
        bloomUpPso = MakePso("PSUp", additive);

        bloomCbSlots = BloomMaxLevels * 2;
        bloomCbStride = (Marshal.SizeOf<BloomConstants>() + 255) & ~255;
        bloomCbFrameStride = (long)bloomCbStride * bloomCbSlots;
        bloomCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(bloomCbFrameStride * dev.FramesInFlight)), ResourceStates.GenericRead);
        bloomCbMapped = bloomCb.Map<byte>(0);
        bloomSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, bloomCbSlots, shaderVisible: true, framesInFlight: dev.FramesInFlight);
        AllocBloomTargets(width, height);
    }

    public void Resize(int width, int height) => AllocBloomTargets(width, height);

    void AllocBloomTargets(int width, int height) {
        for (int i = 0; i < BloomMaxLevels; i++) {
            if (bloomLevels[i] is { IsPlaced: false }) bloomLevels[i].Dispose();
            bloomLevels[i] = null;
        }
        int w = System.Math.Max(1, width / 2), h = System.Math.Max(1, height / 2);
        bloomLevelCount = 0;
        for (int i = 0; i < BloomMaxLevels && w >= 8 && h >= 8; i++) {
            bloomLevels[i] = Dx12RenderTargetPool.AllocOrPool(dev, $"bloomL{i}", w, h,
                Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: false);
            bloomLevelCount++;
            w = System.Math.Max(1, w / 2);
            h = System.Math.Max(1, h / 2);
        }
    }

    unsafe void DrawBloom(Dx12OffscreenTarget src, float threshold, float knee) {
        if (bloomLevelCount == 0) return;
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        int slot = 0;

        void Pass(ID3D12PipelineState pso, Dx12OffscreenTarget passSrc, Dx12OffscreenTarget dst, float threshold, float knee) {
            int s = slot++;
            *(BloomConstants*)(bloomCbMapped + BloomCbFrameOffset + s * bloomCbStride) = new BloomConstants {
                TexelSize = new Vector2(1f / passSrc.Width, 1f / passSrc.Height), Threshold = threshold, Knee = knee,
            };
            passSrc.ColorToShaderResource();
            dev.Device.CopyDescriptorsSimple(1, bloomSrvVisible.Cpu(s), passSrc.ColorSrvCpu, heapType);
            dst.RenderColorOnly(cl => {
                cl.SetGraphicsRootSignature(bloomRootSig);
                cl.SetPipelineState(pso);
                cl.SetDescriptorHeaps(bloomSrvVisible.Heap);
                cl.SetGraphicsRootConstantBufferView(0, bloomCb.GPUVirtualAddress + (ulong)(BloomCbFrameOffset + s * bloomCbStride));
                cl.SetGraphicsRootDescriptorTable(1, bloomSrvVisible.Gpu(s));
                cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                cl.DrawInstanced(3, 1, 0, 0);
            });
        }

        Pass(bloomDownThresholdPso, src, bloomLevels[0], threshold, knee);
        for (int i = 1; i < bloomLevelCount; i++)
            Pass(bloomDownPso, bloomLevels[i - 1], bloomLevels[i], 0f, 0f);

        for (int i = bloomLevelCount - 2; i >= 0; i--)
            Pass(bloomUpPso, bloomLevels[i + 1], bloomLevels[i], 0f, 0f);

        bloomLevels[0].ColorToShaderResource();
    }

    unsafe void DumpMeteredLuminance(PostProcessSettings pf) {
        var footprints = new Vortice.Direct3D12.PlacedSubresourceFootPrint[1];
        var rowCounts = new uint[1]; var rowSizes = new ulong[1];
        dev.Device.GetCopyableFootprints(lumTarget.RenderTarget.Description, 0, 1, 0,
            footprints, rowCounts, rowSizes, out ulong totalBytes);
        Vortice.Direct3D12.PlacedSubresourceFootPrint fp = footprints[0];
        using ID3D12Resource rb = dev.Device.CreateCommittedResource(
            Vortice.Direct3D12.HeapProperties.ReadbackHeapProperties, Vortice.Direct3D12.HeapFlags.None,
            Vortice.Direct3D12.ResourceDescription.Buffer(totalBytes), ResourceStates.CopyDest);
        lumTarget.ColorToRenderTarget();
        dev.ExecuteSyncImmediate(cl => {
            cl.ResourceBarrierTransition(lumTarget.RenderTarget, ResourceStates.RenderTarget, ResourceStates.CopySource);
            cl.CopyTextureRegion(new Vortice.Direct3D12.TextureCopyLocation(rb, fp), 0, 0, 0,
                new Vortice.Direct3D12.TextureCopyLocation(lumTarget.RenderTarget, 0), null);
            cl.ResourceBarrierTransition(lumTarget.RenderTarget, ResourceStates.CopySource, ResourceStates.RenderTarget);
        });
        Half* p = rb.Map<Half>(0);
        float avgLum = (float)p[0];
        rb.Unmap(0);
        float greyEv = MathF.Log2(MathF.Max(avgLum, 1e-8f)) - MathF.Log2(0.18f * 1.2f);
        float legacyEv = MathF.Log2(MathF.Max(avgLum, 1e-6f)) + 3f - 1f;
        Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[EXP-DBG] geomean avgLum={avgLum:0.000000}  greyAnchorEV={greyEv:0.00}  legacyEV={legacyEv:0.00}  " +
            $"limits=[{pf.AutoExposureLimitMin},{pf.AutoExposureLimitMax}]  " +
            $"M(greyClamped)={1f / (1.2f * MathF.Pow(2f, Math.Clamp(greyEv, pf.AutoExposureLimitMin, pf.AutoExposureLimitMax))):0.00000000}"));
    }

    unsafe void DumpAdaptedEv(Dx12OffscreenTarget t, PostProcessSettings pf) {
        var footprints = new Vortice.Direct3D12.PlacedSubresourceFootPrint[1];
        var rowCounts = new uint[1]; var rowSizes = new ulong[1];
        dev.Device.GetCopyableFootprints(t.RenderTarget.Description, 0, 1, 0,
            footprints, rowCounts, rowSizes, out ulong totalBytes);
        Vortice.Direct3D12.PlacedSubresourceFootPrint fp = footprints[0];
        using ID3D12Resource rb = dev.Device.CreateCommittedResource(
            Vortice.Direct3D12.HeapProperties.ReadbackHeapProperties, Vortice.Direct3D12.HeapFlags.None,
            Vortice.Direct3D12.ResourceDescription.Buffer(totalBytes), ResourceStates.CopyDest);
        t.ColorToRenderTarget();
        dev.ExecuteSyncImmediate(cl => {
            cl.ResourceBarrierTransition(t.RenderTarget, ResourceStates.RenderTarget, ResourceStates.CopySource);
            cl.CopyTextureRegion(new Vortice.Direct3D12.TextureCopyLocation(rb, fp), 0, 0, 0,
                new Vortice.Direct3D12.TextureCopyLocation(t.RenderTarget, 0), null);
            cl.ResourceBarrierTransition(t.RenderTarget, ResourceStates.CopySource, ResourceStates.PixelShaderResource);
        });
        Half* p = rb.Map<Half>(0);
        float adaptedEv = (float)p[0];
        rb.Unmap(0);
        Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[EMA-DBG] frame={++emaDebugFrame}  adaptedEV={adaptedEv:0.0000}  dt={(float)Time.DeltaTime:0.0000}  " +
            $"speedUp={pf.AutoExposureSpeedDarkToLight}  speedDown={pf.AutoExposureSpeedLightToDark}"));
    }

    unsafe void RecordHistogramMeter(Dx12FrameContext ctx, Dx12OffscreenTarget hdr, ref CpuDescriptorHandle meteredEvSrv) {
        BuildHistogram();
        var pf = ctx.PostFX;
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;

        ID3D12Resource evOut = histEvA, evPrev = histEvB;

        uint gridW = (uint)Math.Min(256, Math.Max(1, ctx.OutputW));
        uint gridH = (uint)Math.Min(144, Math.Max(1, ctx.OutputH));

        const float minLogLum = -10f, logLumRange = 24f;

        bool reset = ctx.DeterministicCapture || !histHistoryValid;

        EnsureHistEvState(ref histEvAState, evOut, ResourceStates.UnorderedAccess);
        EnsureHistEvState(ref histEvBState, evPrev, ResourceStates.PixelShaderResource);

        histCb.Write(new HistConstants {
            SrcWidth = gridW, SrcHeight = gridH, MinLogLum = minLogLum, InvLogLumRange = 1f / logLumRange,
            MeteringMode = (float)(int)pf.MeteringMode, LuxMeterAnchor = 8.0f,
            LimitMin = pf.AutoExposureLimitMin, LimitMax = pf.AutoExposureLimitMax,
            FilterMin = pf.HistogramFilterMin, FilterMax = pf.HistogramFilterMax,
            DeltaTime = (float)Time.DeltaTime,
            SpeedDarkToLight = pf.AutoExposureSpeedDarkToLight, SpeedLightToDark = pf.AutoExposureSpeedLightToDark,
            Reset = reset ? 1f : 0f,
        });

        dev.Device.CopyDescriptorsSimple(1, histBuildHeap.Cpu(1), hdr.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, histResolveHeap.Cpu(1), histEvBSrv, heapType);
        dev.Device.CopyDescriptorsSimple(1, histResolveHeap.Cpu(2), histEvAUav, heapType);

        dev.ExecuteSync(cl => {
            cl.SetComputeRootSignature(histClearRootSig);
            cl.SetPipelineState(histClearPso);
            cl.SetDescriptorHeaps(histClearHeap.Heap);
            cl.SetComputeRootConstantBufferView(0, histCb.Gpu);
            cl.SetComputeRootDescriptorTable(1, histClearHeap.Gpu(0));
            cl.Dispatch((HistogramBins + 255) / 256, 1, 1);
            cl.ResourceBarrierUnorderedAccessView(histogramBuffer);

            cl.SetComputeRootSignature(histBuildRootSig);
            cl.SetPipelineState(histBuildPso);
            cl.SetDescriptorHeaps(histBuildHeap.Heap);
            cl.SetComputeRootConstantBufferView(0, histCb.Gpu);
            cl.SetComputeRootDescriptorTable(1, histBuildHeap.Gpu(0));
            cl.Dispatch((gridW + 15) / 16, (gridH + 15) / 16, 1);
            cl.ResourceBarrierUnorderedAccessView(histogramBuffer);

            cl.SetComputeRootSignature(histResolveRootSig);
            cl.SetPipelineState(histResolvePso);
            cl.SetDescriptorHeaps(histResolveHeap.Heap);
            cl.SetComputeRootConstantBufferView(0, histCb.Gpu);
            cl.SetComputeRootDescriptorTable(1, histResolveHeap.Gpu(0));
            cl.Dispatch(1, 1, 1);
        });

        EnsureHistEvState(ref histEvAState, evOut, ResourceStates.PixelShaderResource);
        meteredEvSrv = histEvASrv;
        histHistoryValid = true;
        (histEvA, histEvB) = (histEvB, histEvA);
        (histEvASrv, histEvBSrv) = (histEvBSrv, histEvASrv);
        (histEvAUav, histEvBUav) = (histEvBUav, histEvAUav);
        (histEvAState, histEvBState) = (histEvBState, histEvAState);
    }

    void EnsureHistEvState(ref ResourceStates state, ID3D12Resource res, ResourceStates to) {
        if (state == to) return;
        ResourceStates from = state;
        dev.ExecuteSync(cl => cl.ResourceBarrierTransition(res, from, to));
        state = to;
    }

    void CreateHistUav(CpuDescriptorHandle dst) {
        dev.Device.CreateUnorderedAccessView(histogramBuffer, null, new UnorderedAccessViewDescription {
            Format = Format.R32_Typeless, ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView {
                FirstElement = 0, NumElements = HistogramBins, StructureByteStride = 0,
                Flags = BufferUnorderedAccessViewFlags.Raw,
            },
        }, dst);
    }

    public unsafe void Record(Dx12FrameContext ctx) {
        Dx12RenderTargetPool.PoolBarrier(ctx.Dev, "bloomL0", "bloomL1", "bloomL2", "bloomL3", "bloomL4", "bloomL5");
        Dx12OffscreenTarget hdr = ctx.SceneColor;
        Dx12OffscreenTarget ldr = ctx.Ldr;
        Dx12OffscreenTarget target = ctx.Target;
        int outputW = ctx.OutputW, outputH = ctx.OutputH;
        bool DeterministicCapture = ctx.DeterministicCapture;
        var doors = ctx.Doors;
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;

        var pf = ctx.PostFX;
        bool manual = manualExposureSet;
        float manualExp = manualExposureValue;
        bool forceAuto = forceAutoExp;
        bool forceHistogram = forceHistogramExp;
        bool useMeter = !manual && (forceAuto || forceHistogram || pf.ExposureMode != ExposureMode.Fixed);
        bool useHistogram = useMeter && (forceHistogram || (!forceAuto && pf.ExposureMode == ExposureMode.AutomaticHistogram));
        float exposureMul = manual ? manualExp : pf.ExposureMultiplier;

        if (!ctx.BarriersDerived) hdr.ColorToShaderResource();

        Dx12OffscreenTarget meteredEvTarget = lumTarget;
        CpuDescriptorHandle meteredEvSrv = lumTarget.ColorSrvCpu;
        if (useHistogram) RecordHistogramMeter(ctx, hdr, ref meteredEvSrv);
        if (useMeter && !useHistogram) {
            bool calibrated = exposureCalibrated;
            bool expDebug = !exposureDebugDumped && Environment.GetEnvironmentVariable("BALLISTIC_DX12_EXPOSURE_DEBUG") == "1";
            bool emaOn = exposureEmaOn;
            if (!lumHistoryValid && !expDebug && emaOn
                && float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_EXPOSURE_EMA_SEED"),
                    System.Globalization.CultureInfo.InvariantCulture, out float seedEv)) {
                lumHistory.Clear(seedEv, seedEv, seedEv);
                lumHistoryValid = true;
            }
            bool reset = DeterministicCapture || !lumHistoryValid || expDebug || !emaOn;
            lumCb.Write(new LumConstants {
                LimitMin = pf.AutoExposureLimitMin, LimitMax = pf.AutoExposureLimitMax,
                Calibrated = expDebug ? 2f : (calibrated ? 1f : 0f),
                DeltaTime = (float)Time.DeltaTime,
                SpeedDarkToLight = pf.AutoExposureSpeedDarkToLight,
                SpeedLightToDark = pf.AutoExposureSpeedLightToDark,
                Reset = reset ? 1f : 0f,
            });
            dev.Device.CopyDescriptorsSimple(1, lumSrvVisible.Cpu(0), hdr.ColorSrvCpu, heapType);
            dev.Device.CopyDescriptorsSimple(1, lumSrvVisible.Cpu(1), lumHistory.ColorSrvCpu, heapType);
            lumHistory.ColorToShaderResource();
            lumTarget.RenderColorOnly(cl => {
                cl.SetGraphicsRootSignature(lumRootSig);
                cl.SetPipelineState(lumPso);
                cl.SetDescriptorHeaps(lumSrvVisible.Heap);
                cl.SetGraphicsRootConstantBufferView(0, lumCb.Gpu);
                cl.SetGraphicsRootDescriptorTable(1, lumSrvVisible.Gpu(0));
                cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                cl.DrawInstanced(3, 1, 0, 0);
            });
            lumTarget.ColorToShaderResource();
            meteredEvTarget = lumTarget;
            if (expDebug) { exposureDebugDumped = true; DumpMeteredLuminance(pf); }
            else { (lumTarget, lumHistory) = (lumHistory, lumTarget); lumHistoryValid = true; }

            if (!expDebug && Environment.GetEnvironmentVariable("BALLISTIC_DX12_EXPOSURE_EMA_DEBUG") == "1")
                DumpAdaptedEv(meteredEvTarget, pf);
            meteredEvSrv = meteredEvTarget.ColorSrvCpu;
        }

        bool bloomOn = doors.Bloom && pf.BloomEnabled;
        if (bloomOn) DrawBloom(hdr, pf.BloomThreshold, pf.BloomKnee);

        bool acesTonemap = acesTonemapEnv;
        float grainTime = DeterministicCapture ? 0f : (ctx.GrainFrame & 1023);
        bool gradeDemo = gradeDemoEnv;
        float contrast = gradeDemo ? 1.12f : pf.Contrast;
        float saturation = gradeDemo ? 1.15f : pf.Saturation;
        float vignette = gradeDemo ? 0.25f : pf.VignetteStrength;
        compositeCb.Write(new CompositeConstants {
            ExposureMul = exposureMul,
            BloomIntensity = bloomOn ? pf.BloomIntensity : 0f,
            AutoExposure = useMeter ? 1f : 0f,
            LegacyMul = pf.Exposure,
            Compensation = pf.ExposureCompensation,
            PadAo = 0f,
            Tonemap = acesTonemap ? 1f : 0f,
            Contrast = contrast, Saturation = saturation,
            Sharpen = float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_SHARPEN"),
                System.Globalization.CultureInfo.InvariantCulture, out float shp) ? shp : pf.Sharpen,
            VignetteStrength = vignette, VignetteRoundness = pf.VignetteRoundness,
            VignetteColor = pf.VignetteColor,
            ChromaticAberration = pf.ChromaticAberration, LensDistortion = pf.LensDistortion,
            FilmGrain = DeterministicCapture ? 0f : pf.FilmGrain, GrainTime = grainTime,
            ScreenSize = new Vector2(outputW, outputH),
        });

        dev.Device.CopyDescriptorsSimple(1, compositeSrvVisible.Cpu(0), hdr.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, compositeSrvVisible.Cpu(1),
            bloomOn && bloomLevelCount > 0 ? bloomLevels[0].ColorSrvCpu : hdr.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, compositeSrvVisible.Cpu(2),
            useMeter ? meteredEvSrv : hdr.ColorSrvCpu, heapType);

        ldr.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(compositeRootSig);
            cl.SetPipelineState(compositePso);
            cl.SetDescriptorHeaps(compositeSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, compositeCb.Gpu);
            cl.SetGraphicsRootDescriptorTable(1, compositeSrvVisible.Gpu(0));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
        if (useMeter && !useHistogram) meteredEvTarget.ColorToRenderTarget();
        target.ColorToRenderTarget();
    }

    public void Dispose() {
        foreach (Dx12OffscreenTarget level in bloomLevels)
            if (level is { IsPlaced: false }) level.Dispose();
        bloomSrvVisible?.Dispose(); bloomCb?.Dispose();
        bloomDownThresholdPso?.Dispose(); bloomDownPso?.Dispose(); bloomUpPso?.Dispose();
        bloomRootSig?.Dispose();
        lumTarget?.Dispose(); lumHistory?.Dispose();
        lumSrvVisible?.Dispose(); lumCb?.Dispose();
        lumPso?.Dispose(); lumRootSig?.Dispose();
        histEvA?.Dispose(); histEvB?.Dispose();
        histogramBuffer?.Dispose(); histCb?.Dispose();
        histClearHeap?.Dispose(); histBuildHeap?.Dispose(); histResolveHeap?.Dispose();
        histClearPso?.Dispose(); histBuildPso?.Dispose(); histResolvePso?.Dispose();
        histClearRootSig?.Dispose(); histBuildRootSig?.Dispose(); histResolveRootSig?.Dispose();
        compositeSrvVisible?.Dispose(); compositeCb?.Dispose();
        compositePso?.Dispose(); compositeRootSig?.Dispose();
    }
}
