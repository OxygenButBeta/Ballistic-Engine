using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;        // PrimitiveTopology
using Vortice.Direct3D12;
using Vortice.Dxc;             // DxcShaderStage
using Vortice.DXGI;            // Format, SampleDescription
using BallisticEngine;         // PostProcessSettings, ExposureMode, Time

namespace BallisticEngine.DX12;

// Final tonemap/grade: the HDR scene color (ctx.SceneColor — `target` natively, or the FSR-upscaled output)
// → the LDR composite output at OUTPUT resolution. Auto-exposure metering (1×1 LumAverage) + bloom run as
// PRIVATE sub-steps INSIDE this pass (called from Record, ping-pong interleaved) — splitting them into their
// own passes would be a restructure, not a move (correctness trap 3), so they stay here.
//
// VERBATIM MOVE (chunk 7 of the pass-graph migration): the bodies of BuildComposite/BuildLumAverage/BuildBloom/
// AllocBloomTargets/DrawBloom/DumpMeteredLuminance/DumpAdaptedEv/DrawComposite are copied unchanged, only
// re-rooted onto `ctx`/this pass's own fields. No logic change → eyeball-unchanged + zero NEW GBV (a MOVE-only
// commit). Copies the Dx12SsaoPass template (the canonical leaf-post pass).
//
// Event = Composite (700). Reads ctx.SceneColor (the FSR/native HDR source the orchestrator + FsrPass resolve)
// in place of the old `hdr` param. (AO is no longer composited here — GTAO multiplies into the deferred ambient.) ctx.GrainFrame
// for the grain phase. Writes the LDR `ldr` target, then restores ctx.Target to RenderTarget (frame-end tidy).
public sealed class Dx12CompositePass : IRenderPass, IDisposable {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.Composite;
    public string Name => "Composite";

    // Composite always runs (it IS the tonemap). The old inline call was unconditional in both branches.
    public bool Enabled(Dx12FrameContext ctx) => true;

    // PHASE-2 V1: reads the resolved scene color (ctx.SceneColor — native = target, FSR = FsrOutput) and the
    // SSAO result, tonemaps + composites + bloom + exposure-metering (private sub-steps) and WRITES the LDR
    // output. Declares reads of BOTH "SceneColor" and "FsrOutput" so an edge forms from whichever upstream
    // writer is active (TAA writes SceneColor in the native path; FSR writes FsrOutput) — both keep Composite
    // last. Also reads the "Ssao" handle (so SSAO is never culled out from under it).
    public void Declare(Dx12PassBuilder b) {
        b.Read(b.Resource("SceneColor"));
        b.Read(b.Resource("FsrOutput"));
        b.Read(b.Resource("Ssao"));
        b.Write(b.Resource("Ldr"));
        // PHASE-2 V3 (chunk 16): Composite's ONE shared-resource head transition is `hdr.ColorToShaderResource()`
        // where hdr == ctx.SceneColor (captured in Record — the resolved scene color: native = target, FSR =
        // fsrOutput, since FsrPass at event 650 already set ctx.SceneColor before Composite at 700). The deriver
        // emits ctx.SceneColor.ColorToShaderResource() — the same concrete target. Derive it. The PRIVATE sub-step
        // transitions (lum ping-pong, bloom mip chain) and the frame-tail target.ColorToRenderTarget() are NOT pass-
        // boundary heads → they stay inline (plan §V3: leave the frame-tail inline, derive only the head).
        b.DeriveBarriers();
        b.Use(Dx12ResourceUsage.SceneColorShaderRead);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CompositeConstants {
        public float ExposureMul; public float BloomIntensity; public float AutoExposure; public float LegacyMul;   // row 0
        public float Compensation; public float PadAo; public float Tonemap; public float Contrast;                 // row 1 (PadAo: was UseAo; AO moved to deferred ambient)
        public float Saturation; public float Sharpen; public float VignetteStrength; public float VignetteRoundness; // row 2
        public float ChromaticAberration; public float LensDistortion; public float FilmGrain; public float GrainTime; // row 3
        public Vector3 VignetteColor; public float Pad3;                                                            // row 4
        public Vector2 ScreenSize; public Vector2 Pad4;                                                             // row 5
    }
    [StructLayout(LayoutKind.Sequential)]
    struct LumConstants {
        public float LimitMin; public float LimitMax; public float Calibrated; public float DeltaTime;
        public float SpeedDarkToLight; public float SpeedLightToDark; public float Reset; public float Pad;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct BloomConstants { public Vector2 TexelSize; public float Threshold; public float Knee; }
    // Histogram auto-exposure (ExposureMode.AutomaticHistogram). Mirrors ExposureHistogram.hlsl's HistConstants.
    [StructLayout(LayoutKind.Sequential)]
    struct HistConstants {
        public uint SrcWidth; public uint SrcHeight; public float MinLogLum; public float InvLogLumRange;     // row 0
        public float MeteringMode; public float LuxMeterAnchor; public float LimitMin; public float LimitMax;  // row 1
        public float FilterMin; public float FilterMax; public float DeltaTime; public float SpeedDarkToLight; // row 2
        public float SpeedLightToDark; public float Reset; public float Pad0; public float Pad1;               // row 3
    }

    readonly Dx12Device dev;

    ID3D12RootSignature compositeRootSig;   // CompositeConstants CBV (b0) + HDR+bloom SRV table + sampler
    ID3D12PipelineState compositePso;
    Dx12FrameCb<CompositeConstants> compositeCb;
    Dx12DescriptorHeap compositeSrvVisible;  // HDR color + bloom + avg-lum, copied per frame

    // Auto-exposure: a 1×1 R16F target holding the metered exposure EV100 (LumAverage.hlsl).
    ID3D12RootSignature lumRootSig;     // LumConstants CBV (b0) + 2 SRVs (t0 HDR, t1 prev-EV) + sampler
    ID3D12PipelineState lumPso;
    // V1b: TWO 1×1 R16F targets, ping-ponged each frame — the meter reads last frame's adapted EV (history)
    // and writes this frame's adapted EV. lumTarget = the one written THIS frame (composite reads it);
    // lumHistory = last frame's. Swapped after the pass. Avoids a per-frame GPU→CPU readback stall.
    Dx12OffscreenTarget lumTarget, lumHistory;
    bool lumHistoryValid;               // V1b: false until the first metered frame populates history → snap (Reset)
    bool exposureDebugDumped;           // V1: one-shot BALLISTIC_DX12_EXPOSURE_DEBUG avgLum readback latch

    // P2 — exposure/tonemap/grade env doors resolved ONCE (was re-read every DrawComposite). Process-scoped
    // A/B switches → byte-identical to the per-frame reads. The debug-only doors (EXPOSURE_DEBUG, EMA_DEBUG,
    // EMA_SEED) stay inline: they're never on the production path and one is a one-shot latch.
    readonly bool manualExposureSet;    // BALLISTIC_DX12_EXPOSURE parses → manual exposure mode
    readonly float manualExposureValue;
    readonly bool forceAutoExp;         // BALLISTIC_DX12_AUTOEXP == "1"
    readonly bool forceHistogramExp;    // BALLISTIC_DX12_EXPOSURE_HISTOGRAM == "1" (force the histogram meter for A/B)
    readonly bool exposureCalibrated;   // BALLISTIC_DX12_EXPOSURE_CALIB != "0"
    readonly bool exposureEmaOn;        // BALLISTIC_DX12_EXPOSURE_EMA != "0"
    readonly bool acesTonemapEnv;       // BALLISTIC_DX12_TONEMAP == "aces"
    readonly bool gradeDemoEnv;         // BALLISTIC_DX12_GRADE_DEMO == "1"
    Dx12DescriptorHeap lumSrvVisible;   // [0]=HDR color SRV, [1]=prev-EV history SRV, copied per frame
    Dx12FrameCb<LumConstants> lumCb;
    int emaDebugFrame;

    // ===== Histogram auto-exposure (ExposureMode.AutomaticHistogram) — the production percentile-clip meter.
    // Three compute kernels (clear → build → resolve, ExposureHistogram.hlsl). A 256-uint RWByteAddressBuffer
    // holds the weighted log-luminance histogram; CSResolve percentile-clips it, averages the surviving band,
    // converts to a metered EV and EMA-eases it into a 1×1 R32F UAV target the composite reads. Two such EV
    // targets are CPU ping-ponged (like lumTarget/lumHistory) so the meter reads last frame's adapted EV
    // without a GPU→CPU readback. All histogram resources are LAZILY created on the first AutomaticHistogram
    // frame — a scene that never uses histogram mode allocates nothing (Fixed/Automatic untouched).
    const int HistogramBins = 256;
    ID3D12RootSignature histClearRootSig, histBuildRootSig, histResolveRootSig;
    ID3D12PipelineState histClearPso, histBuildPso, histResolvePso;
    ID3D12Resource histogramBuffer;                 // 256 uints (1024 bytes), UAV (u0)
    Dx12FrameCb<HistConstants> histCb;
    Dx12DescriptorHeap histClearHeap;               // [0] = histogram UAV
    Dx12DescriptorHeap histBuildHeap;               // [0] = histogram UAV, [1] = HDR SRV
    Dx12DescriptorHeap histResolveHeap;             // [0] = histogram UAV, [1] = prev-EV SRV, [2] = adapted-EV UAV
    // Ping-pong 1×1 R32F EV targets (this frame's adapted EV + last frame's, swapped each frame). Plain
    // committed resources (not Dx12OffscreenTarget — those force an RTV+RenderTarget initial state; here the
    // resource is a UAV that the composite reads as an SRV). Each carries its own SRV + UAV descriptor.
    ID3D12Resource histEvA, histEvB;
    CpuDescriptorHandle histEvASrv, histEvAUav, histEvBSrv, histEvBUav;
    ResourceStates histEvAState, histEvBState;
    bool histBuilt;                                  // lazily-created resources exist
    bool histHistoryValid;                           // false until the first histogram frame populates history → snap

    // Bloom: progressive dual-filter mip pyramid (Jimenez/COD), fed into the composite (Bloom.hlsl).
    // A chain of half-res→quarter→… HDR targets: downsample (level 0 thresholds + Karis), then tent-upsample
    // each smaller level ADDITIVELY onto the next larger one. The half-res level-0 result is what composite adds.
    const int BloomMaxLevels = 6;
    ID3D12RootSignature bloomRootSig;   // BloomConstants CBV (b0) + 1 source SRV (t0) + sampler
    ID3D12PipelineState bloomDownThresholdPso, bloomDownPso, bloomUpPso;  // up PSO uses additive (One/One) blend
    readonly Dx12OffscreenTarget[] bloomLevels = new Dx12OffscreenTarget[BloomMaxLevels];
    int bloomLevelCount;
    ID3D12Resource bloomCb;
    unsafe byte* bloomCbMapped;
    int bloomCbStride;                  // 256-aligned per-pass slot stride; one slot per down + up draw
    int bloomCbSlots;                   // total CB/SRV slots provisioned (down chain + up chain)
    long bloomCbFrameStride;            // P0b: bytes per frame slab (stride*slots); buffer ×FramesInFlight
    long BloomCbFrameOffset => (long)dev.FrameSlot * bloomCbFrameStride;   // 0 when overlap off
    Dx12DescriptorHeap bloomSrvVisible; // source SRV per pass (one per down + up draw)

    // VERBATIM BuildComposite (which chained BuildLumAverage → BuildBloom). Allocates everything once.
    public unsafe Dx12CompositePass(Dx12Device device, int width, int height) {
        dev = device;
        // P2 — resolve the per-frame exposure/tonemap/grade env doors once (see field comments).
        manualExposureSet  = float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_EXPOSURE"),
                                 System.Globalization.CultureInfo.InvariantCulture, out manualExposureValue);
        forceAutoExp       = Environment.GetEnvironmentVariable("BALLISTIC_DX12_AUTOEXP") == "1";
        forceHistogramExp  = Environment.GetEnvironmentVariable("BALLISTIC_DX12_EXPOSURE_HISTOGRAM") == "1";
        exposureCalibrated = Environment.GetEnvironmentVariable("BALLISTIC_DX12_EXPOSURE_CALIB") != "0";
        exposureEmaOn      = Environment.GetEnvironmentVariable("BALLISTIC_DX12_EXPOSURE_EMA") != "0";
        acesTonemapEnv     = Environment.GetEnvironmentVariable("BALLISTIC_DX12_TONEMAP") == "aces";
        gradeDemoEnv       = Environment.GetEnvironmentVariable("BALLISTIC_DX12_GRADE_DEMO") == "1";
        // CompositeConstants CBV (b0) + 3-SRV table (HDR t0, bloom t1, avg-lum t2) + clamp sampler s0.
        // (The old AO t3 slot is gone — GTAO now multiplies into ambient in deferred lighting, not here.)
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
        // PSO cache (proof migration). "Composite" = the disk-library key. Byte-identical hit.
        compositePso = dev.CreateGraphicsPso(new GraphicsPipelineStateDescription {
            RootSignature = compositeRootSig, VertexShader = vs, PixelShader = ps, InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.ColorFormat },   // LDR output
            DepthStencilFormat = Format.Unknown, SampleDescription = new SampleDescription(1, 0),
        }, "Composite");

        compositeCb = new Dx12FrameCb<CompositeConstants>(dev);
        compositeSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 3, shaderVisible: true, framesInFlight: dev.FramesInFlight);

        BuildLumAverage();
        BuildBloom(width, height);
    }

    unsafe void BuildLumAverage() {
        // LumConstants CBV (b0) + 2 SRVs (t0 HDR scene, t1 prev-EV history) + clamp sampler; outputs the 1×1
        // adapted-EV100 target. The 2 SRVs are one contiguous table range (t0..t1) over the visible heap.
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
        lumHistory = new Dx12OffscreenTarget(dev, 1, 1, withDepth: false,  // V1b: ping-pong partner (prev adapted EV)
            colorFormat: Format.R16_Float, colorReadable: true);
        lumSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 2, shaderVisible: true, framesInFlight: dev.FramesInFlight);

        lumCb = new Dx12FrameCb<LumConstants>(dev);
    }

    // Lazily build the histogram auto-exposure pipeline (three compute kernels + buffer + ping-pong EV targets).
    // Called on the first AutomaticHistogram frame so Fixed/Automatic scenes never pay for it.
    unsafe void BuildHistogram() {
        if (histBuilt) return;
        var samp = new StaticSamplerDescription(ShaderVisibility.All, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        // CSClear: CBV b0 + UAV table (histogram u0).
        var cbvB0 = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var uavHist = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 0);
        histClearRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] {
                cbvB0, new RootParameter1(new RootDescriptorTable1(uavHist), ShaderVisibility.All) })));
        // CSBuild: CBV b0 + table[ UAV u0(hist), SRV t0(HDR) ] + sampler s0. The histogram UAV (u0) and the HDR
        // SRV (t0) sit in ONE descriptor table (UAVs and SRVs may share a table range when contiguous in the
        // heap); build the table as a UAV range (u0) then an SRV range (t0) over two consecutive heap slots.
        var buildUav = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, baseShaderRegister: 0, registerSpace: 0, offsetInDescriptorsFromTableStart: 0);
        var buildSrv = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 0, registerSpace: 0, offsetInDescriptorsFromTableStart: 1);
        histBuildRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] {
                cbvB0, new RootParameter1(new RootDescriptorTable1(buildUav, buildSrv), ShaderVisibility.All) },
                new[] { samp })));
        // CSResolve: CBV b0 + table[ UAV u0(hist) @0, SRV t1(prevEV) @1, UAV u1(adaptedEV) @2 ].
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

        // The 256-uint histogram (default heap, UAV). CSClear zeroes it each frame, so the initial state's
        // contents don't matter.
        histogramBuffer = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(HistogramBins * 4), ResourceFlags.AllowUnorderedAccess),
            ResourceStates.UnorderedAccess);
        histogramBuffer.Name = "ExposureHistogram";

        histCb = new Dx12FrameCb<HistConstants>(dev);
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        histClearHeap   = new Dx12DescriptorHeap(dev, heapType, 1, shaderVisible: true, framesInFlight: dev.FramesInFlight);
        histBuildHeap   = new Dx12DescriptorHeap(dev, heapType, 2, shaderVisible: true, framesInFlight: dev.FramesInFlight);
        histResolveHeap = new Dx12DescriptorHeap(dev, heapType, 3, shaderVisible: true, framesInFlight: dev.FramesInFlight);
        // The histogram UAV (u0, raw buffer) is STATIC (same buffer every frame) — create it once into slot 0 of
        // every heap, in every N-buffer slab. Only the per-frame SRVs (HDR/prevEV) and the evOut UAV are copied
        // per frame. CpuPhysical addresses the physical slot in a given slab (HiZ pattern).
        for (int slab = 0; slab < dev.FramesInFlight; slab++) {
            CreateHistUav(histClearHeap.CpuPhysical(slab * 1 + 0));
            CreateHistUav(histBuildHeap.CpuPhysical(slab * 2 + 0));
            CreateHistUav(histResolveHeap.CpuPhysical(slab * 3 + 0));
        }

        // The ping-pong 1×1 R32F EV targets + their SRV/UAV descriptors (in the non-shader-visible SrvStore).
        (histEvA, histEvASrv, histEvAUav) = MakeEvTarget();
        (histEvB, histEvBSrv, histEvBUav) = MakeEvTarget();
        histEvAState = histEvBState = ResourceStates.UnorderedAccess;
        histBuilt = true;
    }

    // A 1×1 R32_Float UAV texture + an SRV (composite/prev-EV read) + a UAV (resolve write), in SrvStore.
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

        // Additive (One/One) blend for the upsample pass — each smaller level is ADDED onto the larger one,
        // exactly the GL fixed-function additive upsample. The down passes overwrite (opaque).
        // NB: build it from the ctor, NOT `var b = BlendDescription.Opaque; b.RenderTarget[0] = …` — Vortice's
        // BlendDescription.RenderTarget is a fixed-buffer whose backing is SHARED across copies, so mutating a
        // copy permanently corrupts BlendDescription.Opaque process-wide (proven by reflection). The ctor is safe.
        var additive = new BlendDescription(Blend.One, Blend.One);   // src=One dest=One op=Add, all channels

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

        // Each down + up draw writes DIFFERENT BloomConstants (its own source texel size) but all read one CB.
        // With the pipelined frame they record into ONE list submitted together, so a single shared CB would let
        // a later CPU write stomp a value an earlier draw still needs. Give each draw its own 256-aligned slot.
        // Max draws/frame = down chain (≤ BloomMaxLevels) + up chain (≤ BloomMaxLevels-1) ≤ 2*BloomMaxLevels.
        bloomCbSlots = BloomMaxLevels * 2;
        bloomCbStride = (Marshal.SizeOf<BloomConstants>() + 255) & ~255;
        // P0b: the bloom CB ring is CPU-written every frame → N-buffer (FramesInFlight slabs) + FrameSlot offset
        // so overlap can't stomp it. The bloomSrvVisible heap is already framesInFlight-aware (auto offset).
        bloomCbFrameStride = (long)bloomCbStride * bloomCbSlots;
        bloomCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(bloomCbFrameStride * dev.FramesInFlight)), ResourceStates.GenericRead);
        bloomCbMapped = bloomCb.Map<byte>(0);
        bloomSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, bloomCbSlots, shaderVisible: true, framesInFlight: dev.FramesInFlight);
        AllocBloomTargets(width, height);
    }

    // Only the half-res bloom ping-pong is resolution-dependent (lum is 1×1). The graph fans Resize out in
    // registration order; the original AllocateResolutionTargets order was bloom→ssao→…, so the composite's
    // bloom alloc moves from first to last — byte-neutral because the allocator reads only the passed size
    // (no cross-pass order dependency, R5).
    public void Resize(int width, int height) => AllocBloomTargets(width, height);

    void AllocBloomTargets(int width, int height) {
        // Bloom mip chain: half, quarter, … down to BloomMaxLevels or until a level would drop below 8px (the
        // GL EnsureChain rule). Each level is an HDR transient (every draw fully writes its DST; level 0 is the
        // composite's sampled result + the up chain's final additive target). AllocOrPool = committed when no pool
        // is active (byte-identical), placed-aliased otherwise. Dispose current fields unless pool-placed.
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

    // Progressive dual-filter bloom (Jimenez/COD). Downsample chain: HDR scene → level 0 (Karis + threshold) →
    // level 1 → … . Upsample chain: tent-filter each smaller level ADDITIVELY (One/One blend PSO) onto the next
    // larger one, level N-1 down to 0. Level 0 (half-res) holds the final bloom the composite adds. `src` (the
    // HDR scene) is already in SRV state from DrawComposite. Each draw owns a distinct CB/SRV slot (pipelined-safe).
    unsafe void DrawBloom(Dx12OffscreenTarget src, float threshold, float knee) {
        if (bloomLevelCount == 0) return;   // viewport too small for even one mip level
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        int slot = 0;

        void Pass(ID3D12PipelineState pso, Dx12OffscreenTarget passSrc, Dx12OffscreenTarget dst, float threshold, float knee) {
            int s = slot++;
            // Source texel size = 1 / the SOURCE level being read (tap spacing is in the source's pixels).
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

        // Downsample: HDR scene → level 0 (threshold + Karis from the volume's threshold/knee), then each level
        // from its predecessor (plain energy-preserving 13-tap, no threshold). Threshold/knee only matter on L0.
        Pass(bloomDownThresholdPso, src, bloomLevels[0], threshold, knee);
        for (int i = 1; i < bloomLevelCount; i++)
            Pass(bloomDownPso, bloomLevels[i - 1], bloomLevels[i], 0f, 0f);

        // Upsample: tent-filter level i+1 additively onto level i, from the smallest up to level 0.
        for (int i = bloomLevelCount - 2; i >= 0; i--)
            Pass(bloomUpPso, bloomLevels[i + 1], bloomLevels[i], 0f, 0f);

        bloomLevels[0].ColorToShaderResource();   // half-res result, ready for the composite to sample
    }

    // VERBATIM DumpMeteredLuminance. V1 one-shot calibration probe: read the 1×1 R16F meter target back to the
    // CPU and print it + the EVs each anchor would produce. BALLISTIC_DX12_EXPOSURE_DEBUG=1.
    unsafe void DumpMeteredLuminance(PostProcessSettings pf) {
        var footprints = new Vortice.Direct3D12.PlacedSubresourceFootPrint[1];
        var rowCounts = new uint[1]; var rowSizes = new ulong[1];
        dev.Device.GetCopyableFootprints(lumTarget.RenderTarget.Description, 0, 1, 0,
            footprints, rowCounts, rowSizes, out ulong totalBytes);
        Vortice.Direct3D12.PlacedSubresourceFootPrint fp = footprints[0];
        using ID3D12Resource rb = dev.Device.CreateCommittedResource(
            Vortice.Direct3D12.HeapProperties.ReadbackHeapProperties, Vortice.Direct3D12.HeapFlags.None,
            Vortice.Direct3D12.ResourceDescription.Buffer(totalBytes), ResourceStates.CopyDest);
        lumTarget.ColorToRenderTarget();   // SRV → RT so the next transition is from a known state
        dev.ExecuteSyncImmediate(cl => {   // readback: flush an open pipelined frame so the copy sees this frame
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

    // VERBATIM DumpAdaptedEv. V1b eye-adaptation trace (BALLISTIC_DX12_EXPOSURE_EMA_DEBUG=1): read back the
    // 1×1 adapted-EV target this frame and log it. DEBUG-ONLY — the readback stalls, gated off by default.
    // `pf` = ctx.PostFX (the inline version read the renderer's PostFX field; here it's passed from Record).
    unsafe void DumpAdaptedEv(Dx12OffscreenTarget t, PostProcessSettings pf) {
        var footprints = new Vortice.Direct3D12.PlacedSubresourceFootPrint[1];
        var rowCounts = new uint[1]; var rowSizes = new ulong[1];
        dev.Device.GetCopyableFootprints(t.RenderTarget.Description, 0, 1, 0,
            footprints, rowCounts, rowSizes, out ulong totalBytes);
        Vortice.Direct3D12.PlacedSubresourceFootPrint fp = footprints[0];
        using ID3D12Resource rb = dev.Device.CreateCommittedResource(
            Vortice.Direct3D12.HeapProperties.ReadbackHeapProperties, Vortice.Direct3D12.HeapFlags.None,
            Vortice.Direct3D12.ResourceDescription.Buffer(totalBytes), ResourceStates.CopyDest);
        t.ColorToRenderTarget();   // PixelShaderResource → RT so the copy transition starts from a known state
        dev.ExecuteSyncImmediate(cl => {   // readback: flush an open pipelined frame so the copy sees this frame
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

    // Histogram auto-exposure (ExposureMode.AutomaticHistogram). Runs three compute kernels — clear → build →
    // resolve — to produce this frame's adapted EV in a 1×1 R32F target, then sets `meteredEvSrv` to that
    // target's SRV so the composite samples it exactly like the legacy meter's R16F target. CPU ping-pongs the
    // two EV targets so the resolve reads last frame's adapted EV (history) without a GPU readback. Under
    // DeterministicCapture the EMA is bypassed (Reset = snap) so paused captures stay byte-identical.
    unsafe void RecordHistogramMeter(Dx12FrameContext ctx, Dx12OffscreenTarget hdr, ref CpuDescriptorHandle meteredEvSrv) {
        BuildHistogram();
        var pf = ctx.PostFX;
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;

        // This frame: write into histEvA (the "out" target), read history from histEvB. State is tracked per
        // FIELD (histEvAState pairs with histEvA, histEvBState with histEvB); the fields swap at the very end.
        ID3D12Resource evOut = histEvA, evPrev = histEvB;

        // Downsample grid for the build: a fixed coarse grid (deterministic, cheap) capped to the output size.
        // 256×144 ≈ 37k samples — plenty to fill 256 bins robustly; the percentile clip cares about the
        // distribution shape, not exact pixel counts.
        uint gridW = (uint)Math.Min(256, Math.Max(1, ctx.OutputW));
        uint gridH = (uint)Math.Min(144, Math.Max(1, ctx.OutputH));

        // Log-luminance window for the 256 bins. [2^-10, 2^14] spans the engine's lux-scaled radiance (very dark
        // interior shadow ~1e-3 .. bright lit surface ~1e4); pixels outside clamp to the end bins (the percentile
        // clip rejects them anyway). MinLogLum=-10, range=24 → InvLogLumRange=1/24.
        const float minLogLum = -10f, logLumRange = 24f;

        // EMA reset (snap, no ease): deterministic capture (the paused-frame oracle) or the first histogram frame
        // (no valid history). Otherwise ease at the volume's stops/sec, brighten-fast / darken-slow.
        bool reset = ctx.DeterministicCapture || !histHistoryValid;

        // The out target must be writable as u1 (UAV); the history target readable as t1 (SRV).
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

        // Per-frame descriptor copies (the static histogram UAV at slot 0 of each heap was created at build).
        dev.Device.CopyDescriptorsSimple(1, histBuildHeap.Cpu(1), hdr.ColorSrvCpu, heapType);     // build:   t0 = HDR scene
        dev.Device.CopyDescriptorsSimple(1, histResolveHeap.Cpu(1), histEvBSrv, heapType);        // resolve: t1 = prev adapted EV (history target)
        dev.Device.CopyDescriptorsSimple(1, histResolveHeap.Cpu(2), histEvAUav, heapType);        // resolve: u1 = adapted EV out

        // The histogram meter (Clear→Build→Resolve) is COMPUTE-ONLY (histogram UAV + 1×1 EV UAVs + HDR SRV read).
        // It is a candidate for the async-compute queue, BUT the HDR scene is in PIXEL_SHADER_RESOURCE here (a
        // graphics-only state the compute queue can't read) — async routing would need HDR left in the combined
        // ALL_SHADER (NON_PIXEL|PIXEL) state before the hand-off first. Until that state change is wired, it stays
        // inline (correct + byte-identical). The N-hand-off infra (Dx12Device.RecordAsyncCompute) is now in place
        // so this becomes a one-line RecordAsyncCompute swap once HDR's pre-hand-off state is widened.
        dev.ExecuteSync(cl => {
            // ---- CSClear: zero the histogram ----
            cl.SetComputeRootSignature(histClearRootSig);
            cl.SetPipelineState(histClearPso);
            cl.SetDescriptorHeaps(histClearHeap.Heap);
            cl.SetComputeRootConstantBufferView(0, histCb.Gpu);
            cl.SetComputeRootDescriptorTable(1, histClearHeap.Gpu(0));
            cl.Dispatch((HistogramBins + 255) / 256, 1, 1);
            cl.ResourceBarrierUnorderedAccessView(histogramBuffer);

            // ---- CSBuild: scatter weighted log-luminance into bins ----
            cl.SetComputeRootSignature(histBuildRootSig);
            cl.SetPipelineState(histBuildPso);
            cl.SetDescriptorHeaps(histBuildHeap.Heap);
            cl.SetComputeRootConstantBufferView(0, histCb.Gpu);
            cl.SetComputeRootDescriptorTable(1, histBuildHeap.Gpu(0));
            cl.Dispatch((gridW + 15) / 16, (gridH + 15) / 16, 1);
            cl.ResourceBarrierUnorderedAccessView(histogramBuffer);

            // ---- CSResolve: percentile-clip → metered EV → EMA → 1×1 EV out ----
            cl.SetComputeRootSignature(histResolveRootSig);
            cl.SetPipelineState(histResolvePso);
            cl.SetDescriptorHeaps(histResolveHeap.Heap);
            cl.SetComputeRootConstantBufferView(0, histCb.Gpu);
            cl.SetComputeRootDescriptorTable(1, histResolveHeap.Gpu(0));
            cl.Dispatch(1, 1, 1);
        });

        // The out target now holds this frame's adapted EV → transition to PixelShaderResource for the composite.
        EnsureHistEvState(ref histEvAState, evOut, ResourceStates.PixelShaderResource);
        meteredEvSrv = histEvASrv;
        histHistoryValid = true;
        // Ping-pong: next frame writes the OTHER target and reads this one as history. Swap the matched
        // (resource, srv, uav, state) tuples together so the field/state pairing stays consistent.
        (histEvA, histEvB) = (histEvB, histEvA);
        (histEvASrv, histEvBSrv) = (histEvBSrv, histEvASrv);
        (histEvAUav, histEvBUav) = (histEvBUav, histEvAUav);
        (histEvAState, histEvBState) = (histEvBState, histEvAState);
    }

    // Transition one of the 1×1 EV targets to `to`, tracking its state in the matching ref field.
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

    // VERBATIM DrawComposite (re-rooted onto ctx). Tonemap the HDR `hdr` (= ctx.SceneColor — native scene
    // target or FSR output) into the LDR `ldr` at OUTPUT resolution. Auto-exposure metering + bloom run first
    // (private sub-steps). The old (bool ssaoOn, Dx12OffscreenTarget hdr) params are now ctx.SceneColor only —
    // AO is no longer composited here (GTAO multiplies into the deferred ambient term).
    public unsafe void Record(Dx12FrameContext ctx) {
        // V2: aliasing barrier + discard the bloom mip levels (the targets Composite PRODUCES). (AO is no longer
        // sampled here — GTAO multiplies into the deferred ambient term, so there is no AO slot to preserve.)
        Dx12RenderTargetPool.PoolBarrier(ctx.Dev, "bloomL0", "bloomL1", "bloomL2", "bloomL3", "bloomL4", "bloomL5");   // no-op when pool off
        Dx12OffscreenTarget hdr = ctx.SceneColor;
        Dx12OffscreenTarget ldr = ctx.Ldr;
        Dx12OffscreenTarget target = ctx.Target;
        int outputW = ctx.OutputW, outputH = ctx.OutputH;
        bool DeterministicCapture = ctx.DeterministicCapture;
        var doors = ctx.Doors;
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;

        // Exposure (P1): physical EV100 from the Exposure volume. Three sources, in priority:
        //   1. BALLISTIC_DX12_EXPOSURE env var — raw multiplier override (escape hatch / deterministic capture).
        //   2. Exposure volume Fixed mode — resolve PostProcessSettings.ExposureMultiplier CPU-side (no meter).
        //   3. Automatic / AutomaticHistogram — run the 1×1 metering pass (writes the metered EV100); the
        //      composite shader turns that EV into the multiplier (same 1/(1.2*2^(EV-comp)) formula).
        var pf = ctx.PostFX;
        bool manual = manualExposureSet;
        float manualExp = manualExposureValue;
        // BALLISTIC_DX12_AUTOEXP=1 forces Automatic metering even with no Exposure volume (A/B test door).
        bool forceAuto = forceAutoExp;
        // forceHistogram (BALLISTIC_DX12_EXPOSURE_HISTOGRAM=1) forces the histogram meter for A/B even with no
        // Exposure volume (like forceAuto but the histogram path).
        bool forceHistogram = forceHistogramExp;
        bool useMeter = !manual && (forceAuto || forceHistogram || pf.ExposureMode != ExposureMode.Fixed);
        // AutomaticHistogram routes through the percentile-clip histogram meter (compute) instead of the 1×1
        // geometric-mean meter. forceAuto (the A/B door, no volume) keeps the legacy geomean path. Fixed/Automatic
        // are BYTE-IDENTICAL (the histogram resources aren't even allocated for them).
        bool useHistogram = useMeter && (forceHistogram || (!forceAuto && pf.ExposureMode == ExposureMode.AutomaticHistogram));
        // Resolved CPU multiplier for the manual / Fixed paths (Automatic resolves it in the shader from the EV).
        float exposureMul = manual ? manualExp : pf.ExposureMultiplier;

        // PHASE-2 V3: skip the manual SceneColor head when derived barriers are active (the graph emitted
        // ctx.SceneColor.ColorToShaderResource() before Record; hdr == ctx.SceneColor). Idempotent either way.
        if (!ctx.BarriersDerived) hdr.ColorToShaderResource();   // HDR source → SRV (for both the lum pass and composite)

        // V1b: the physical 1×1 target holding THIS frame's adapted EV (captured pre-swap so the composite
        // SRV + the frame-end RT transition keep pointing at it after the ping-pong swaps the fields).
        Dx12OffscreenTarget meteredEvTarget = lumTarget;
        // The 1×1 adapted-EV SRV the composite samples. Defaults to the legacy ping-pong target; histogram mode
        // overrides it with the compute meter's R32F EV target. Resolved below so the composite SRV copy is
        // path-agnostic.
        CpuDescriptorHandle meteredEvSrv = lumTarget.ColorSrvCpu;
        if (useHistogram) RecordHistogramMeter(ctx, hdr, ref meteredEvSrv);
        if (useMeter && !useHistogram) {
            // Auto-exposure metering: reduce the HDR source to a 1×1 adapted EV100 (LumAverage.hlsl).
            // V1: the meter is grey-anchored (self-calibrating to the lux-scaled DX12 radiance) by default;
            // BALLISTIC_DX12_EXPOSURE_CALIB=0 restores the legacy photometric anchor (the pre-V1 blow-out) for A/B.
            bool calibrated = exposureCalibrated;
            // V1 diagnostic: BALLISTIC_DX12_EXPOSURE_DEBUG=1 makes the meter emit raw geomean luminance into the
            // 1×1 target (Calibrated=2), read back once below to ground-truth the calibration constant.
            bool expDebug = !exposureDebugDumped && Environment.GetEnvironmentVariable("BALLISTIC_DX12_EXPOSURE_DEBUG") == "1";
            // V1b eye-adaptation EMA. Reset (snap to metered EV, no ease) when: deterministic capture (keeps
            // paused frames byte-identical to the pre-V1b instantaneous meter — the oracle), the FIRST metered
            // frame (history uninitialized — easing up from EV 0 would flash dark→correct), or the debug probe
            // (it emits raw avgLum, which must not be temporally eased). Eased frames need the real frame dt.
            // BALLISTIC_DX12_EXPOSURE_EMA=0 forces instantaneous (the pre-V1b behaviour) for A/B.
            bool emaOn = exposureEmaOn;
            // V1b debug: BALLISTIC_DX12_EXPOSURE_EMA_SEED=<ev> seeds the FIRST frame's history to a deliberately-
            // off EV (instead of snapping to metered) so the easing curve toward the true metered EV is
            // observable headlessly. Debug-only; never set on the production path.
            if (!lumHistoryValid && !expDebug && emaOn
                && float.TryParse(Environment.GetEnvironmentVariable("BALLISTIC_DX12_EXPOSURE_EMA_SEED"),
                    System.Globalization.CultureInfo.InvariantCulture, out float seedEv)) {
                lumHistory.Clear(seedEv, seedEv, seedEv);   // R16F: only R matters (the seeded prev EV)
                lumHistoryValid = true;                     // skip the first-frame snap so the seeded value eases
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
            // t0 = HDR scene; t1 = last frame's adapted EV (the ping-pong partner, already in PixelShaderResource).
            dev.Device.CopyDescriptorsSimple(1, lumSrvVisible.Cpu(0), hdr.ColorSrvCpu, heapType);
            dev.Device.CopyDescriptorsSimple(1, lumSrvVisible.Cpu(1), lumHistory.ColorSrvCpu, heapType);
            lumHistory.ColorToShaderResource();   // ensure the history is sampleable as t1 (no-op after frame 1)
            lumTarget.RenderColorOnly(cl => {
                cl.SetGraphicsRootSignature(lumRootSig);
                cl.SetPipelineState(lumPso);
                cl.SetDescriptorHeaps(lumSrvVisible.Heap);
                cl.SetGraphicsRootConstantBufferView(0, lumCb.Gpu);
                cl.SetGraphicsRootDescriptorTable(1, lumSrvVisible.Gpu(0));
                cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                cl.DrawInstanced(3, 1, 0, 0);
            });
            lumTarget.ColorToShaderResource();    // composite reads it AND it becomes next frame's history t1
            meteredEvTarget = lumTarget;          // the target holding THIS frame's adapted EV (pre-swap)
            if (expDebug) { exposureDebugDumped = true; DumpMeteredLuminance(pf); }
            // V1b ping-pong: this frame's written target becomes next frame's history; the old history (now in
            // PixelShaderResource) becomes next frame's render target. The composite + the frame-end RT
            // transition below reference `meteredEvTarget` (NOT lumTarget) so the swap doesn't misroute them.
            else { (lumTarget, lumHistory) = (lumHistory, lumTarget); lumHistoryValid = true; }
            // V1b debug trace (off by default; stalls — never on the production path): log the adapted EV.
            if (!expDebug && Environment.GetEnvironmentVariable("BALLISTIC_DX12_EXPOSURE_EMA_DEBUG") == "1")
                DumpAdaptedEv(meteredEvTarget, pf);
            meteredEvSrv = meteredEvTarget.ColorSrvCpu;   // composite samples the legacy ping-pong EV target
        }

        // Bloom: progressive mip-pyramid bright-pass + blur of the HDR into the half-res level 0. The env door
        // is the hard master switch (BALLISTIC_DX12_BLOOM=0); within that, the Bloom VOLUME drives it (enable +
        // threshold + knee + intensity), so editing the override in the inspector actually changes the render.
        bool bloomOn = doors.Bloom && pf.BloomEnabled;
        if (bloomOn) DrawBloom(hdr, pf.BloomThreshold, pf.BloomKnee);

        // Tonemap: AgX by default (graceful highlight desaturation, the "less çiğ" look); ACES via the door.
        bool acesTonemap = acesTonemapEnv;
        // Film grain is time-dependent → frozen (0) under deterministic capture so paused frames stay diffable.
        float grainTime = DeterministicCapture ? 0f : (ctx.GrainFrame & 1023);
        // Stylistic grade comes from the volume stack (all neutral by default). BALLISTIC_DX12_GRADE_DEMO=1 is
        // an A/B door that applies a mild cinematic film look (a sensible starting grade) when no ColorAdjustments
        // volume is authored — proves the grade chain and shows what a light touch buys.
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
            PadAo = 0f,   // (was UseAo) AO is applied in deferred lighting now
            Tonemap = acesTonemap ? 1f : 0f,
            // Stylistic grade (all neutral by default → byte-identical when untouched); ported from the GL composite.
            Contrast = contrast, Saturation = saturation,
            Sharpen = pf.Sharpen,
            VignetteStrength = vignette, VignetteRoundness = pf.VignetteRoundness,
            // pf.VignetteColor is System.Numerics.Vector3 (engine math = Numerics; the inline ToNumerics(Vector3)
            // was an identity copy) → assign direct.
            VignetteColor = pf.VignetteColor,
            ChromaticAberration = pf.ChromaticAberration, LensDistortion = pf.LensDistortion,
            FilmGrain = DeterministicCapture ? 0f : pf.FilmGrain, GrainTime = grainTime,
            ScreenSize = new Vector2(outputW, outputH),
        });

        dev.Device.CopyDescriptorsSimple(1, compositeSrvVisible.Cpu(0), hdr.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, compositeSrvVisible.Cpu(1),
            bloomOn && bloomLevelCount > 0 ? bloomLevels[0].ColorSrvCpu : hdr.ColorSrvCpu, heapType);   // bloom slot (half-res level 0)
        dev.Device.CopyDescriptorsSimple(1, compositeSrvVisible.Cpu(2),
            useMeter ? meteredEvSrv : hdr.ColorSrvCpu, heapType);   // adapted-EV slot (Automatic/AutomaticHistogram)

        ldr.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(compositeRootSig);
            cl.SetPipelineState(compositePso);
            cl.SetDescriptorHeaps(compositeSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, compositeCb.Gpu);
            cl.SetGraphicsRootDescriptorTable(1, compositeSrvVisible.Gpu(0));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
        // Restore THIS frame's adapted-EV target (just consumed as the composite SRV) to RenderTarget — the
        // legacy V1 frame-end tidy. With the V1b ping-pong it's now the `lumHistory` field; next frame the
        // meter reads it as the t1 SRV (its own ColorToShaderResource handles that transition) and renders
        // into the OTHER target. The state tracker makes either order valid; this keeps it consistent.
        // (Histogram mode owns its own EV-target transitions in RecordHistogramMeter — skip the legacy tidy.)
        if (useMeter && !useHistogram) meteredEvTarget.ColorToRenderTarget();
        // Restore the INTERNAL scene target to RenderTarget for next frame's geometry/deferred pass (FSR
        // left it in PixelShaderResource; in the native path hdr == target). fsrOutput stays in shader-read
        // — RunFsr transitions it to UAV next frame from any state.
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
        // Histogram auto-exposure (lazily built; null when AutomaticHistogram was never used).
        histEvA?.Dispose(); histEvB?.Dispose();
        histogramBuffer?.Dispose(); histCb?.Dispose();
        histClearHeap?.Dispose(); histBuildHeap?.Dispose(); histResolveHeap?.Dispose();
        histClearPso?.Dispose(); histBuildPso?.Dispose(); histResolvePso?.Dispose();
        histClearRootSig?.Dispose(); histBuildRootSig?.Dispose(); histResolveRootSig?.Dispose();
        compositeSrvVisible?.Dispose(); compositeCb?.Dispose();
        compositePso?.Dispose(); compositeRootSig?.Dispose();
    }
}
