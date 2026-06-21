using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;        // PrimitiveTopology
using Vortice.Direct3D12;
using Vortice.Dxc;             // DxcShaderStage
using Vortice.DXGI;            // Format, SampleDescription

namespace BallisticEngine.DX12;

// Temporal anti-aliasing: blend this frame's jittered color with the reprojected history (motion-vector
// guided), ping-ponging two HDR history buffers, then copy the resolved color back into the scene target.
// TAA IS the AA in this renderer (the MSAA path was deleted). Mutually exclusive with FSR — TaaPass runs only
// in the NATIVE path (Enabled = !ctx.FsrActive).
//
// VERBATIM MOVE (chunk 7 of the pass-graph migration): the bodies of BuildTaa/AllocTaaTargets/DrawTaa are
// copied unchanged, only re-rooted onto `ctx`/this pass's own fields. No logic change → eyeball-unchanged +
// zero NEW GBV (a MOVE-only commit). Copies the Dx12SsaoPass template.
//
// Event = PostProcess (650), registered AFTER SSAO so it runs after the AO pass; the Composite pass (event
// 700) runs after this, reading the resolved scene color (ctx.SceneColor = ctx.Target, which DrawTaa wrote).
//
// TAA-SKIPPED RESET (trap 6 / R-NEW-9): when TAA is disabled in the native path, the inline code did
// `taaHistoryValid = false` so the history is fresh when TAA turns back on. The pass owns taaHistoryValid, so
// the pass owns the reset: Enabled = !FsrActive (TAA participates in the native path), and Record does the
// real TAA only when ctx.TaaActive — otherwise it just resets the flag. Under DETERMINISTIC (bal render),
// TaaActive is false (temporal is pass-through) so Record only resets — byte-invisible to the golden gate;
// the regime-(b) motion-dump exercises the real TAA.
public sealed class Dx12TaaPass : IRenderPass, IDisposable {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.PostProcess;
    public string Name => "TAA";

    // TAA runs in the NATIVE path only (FSR replaces it). Even when TaaActive is false (paused/deterministic/
    // minimal), Record still runs here to reset the pass-owned history-valid flag — so Enabled is !FsrActive,
    // NOT TaaActive.
    public bool Enabled(Dx12FrameContext ctx) => !ctx.FsrActive;

    // PHASE-2 V1: reads the G-buffer (motion vectors live in a G-buffer color RT) and read-modify-writes the HDR
    // scene color (resolves current+history into the pass-owned ping-pong, then CopyColorFrom back into
    // `target`). The pass-owned history targets are IMPORTED (never aliased) — V2 concern; not declared here in
    // V1 (handles are 1:1 concrete; the history is pass-private and orchestrator-immobile).
    public void Declare(Dx12PassBuilder b) {
        b.Read(b.Resource("GBuffer"));
        b.ReadWrite(b.Resource("SceneColor"));
        // PHASE-2 V3 (chunk 16): TAA's ONE shared-resource head transition is `target.ColorToShaderResource()`
        // where target == ctx.SceneColor (the native-path scene color it reads; FSR hasn't run, TAA is the
        // native path) — derive it as SceneColorShaderRead. The pass-private history.ColorToShaderResource() and
        // the motion RT (already PSR from gbuffer.ToShaderResource upstream) are NOT pass-boundary heads → stay
        // inline. The derived emit fires before Record whenever the pass is enabled (!FsrActive), even when
        // TaaActive is false (Record early-returns after resetting the history flag) — a redundant idempotent
        // SceneColor→PSR transition, harmless (the ch15 Transparents idempotent-emit gotcha).
        b.DeriveBarriers();
        b.Use(Dx12ResourceUsage.SceneColorShaderRead);
        // C5: TAA now also reads the G-buffer depth (t3) for closest-depth velocity dilation. Declare it so the
        // barrier-deriver emits the depth→PSR head transition when the graph-barriers door is on (the inline
        // gbuffer.DepthToShaderResource() is skipped under BarriersDerived). Without this, depth could be sampled
        // in DepthRead state when a prior depth-writer (Sky/Transparents) was the last to touch it.
        b.Use(Dx12ResourceUsage.GBufferDepthShaderRead);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TaaConstants { public float Feedback; public float ValidHistory; public Vector2 TexelSize; }

    readonly Dx12Device dev;
    ID3D12RootSignature taaRootSig;     // TaaConstants CBV(b0) + 3-SRV table(current/history/motion) + sampler
    ID3D12PipelineState taaPso;
    Dx12FrameCb<TaaConstants> taaCb;    // P0b: N-buffered (FrameSlot-offset)
    Dx12OffscreenTarget taaHistoryA, taaHistoryB;   // ping-pong accumulated HDR history
    Dx12OffscreenTarget taaResolved;                // this frame's TAA output (→ history + copied to target)
    Dx12DescriptorHeap taaSrvVisible;   // 3 SRVs per frame
    bool taaWriteB;                     // ping-pong toggle
    bool taaHistoryValid;

    // VERBATIM BuildTaa + the trailing AllocTaaTargets.
    public unsafe Dx12TaaPass(Dx12Device device, int width, int height) {
        dev = device;
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 4, baseShaderRegister: 0);  // C5: +t3 depth (velocity dilation)
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var samp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        taaRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { samp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("Taa.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "Taa.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "Taa.hlsl");
        taaPso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = taaRootSig, VertexShader = vs, PixelShader = ps, InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat }, DepthStencilFormat = Format.Unknown,
            SampleDescription = new SampleDescription(1, 0),
        });

        taaCb = new Dx12FrameCb<TaaConstants>(dev);
        taaSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 4, shaderVisible: true, framesInFlight: dev.FramesInFlight);
        AllocTargets(width, height);
    }

    // VERBATIM AllocTaaTargets. The graph fans Resize out in registration order; TAA's original slot in
    // AllocateResolutionTargets was 5th (bloom→ssao→ssr→ssgi→taa→…). The allocator reads only the passed size
    // (no cross-pass order dependency), so its position is byte-neutral (R5).
    public void Resize(int width, int height) => AllocTargets(width, height);

    void AllocTargets(int width, int height) {
        taaHistoryA?.Dispose(); taaHistoryB?.Dispose(); taaResolved?.Dispose();
        taaHistoryA = new Dx12OffscreenTarget(dev, width, height, withDepth: false, colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
        taaHistoryB = new Dx12OffscreenTarget(dev, width, height, withDepth: false, colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
        taaResolved = new Dx12OffscreenTarget(dev, width, height, withDepth: false, colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
        taaHistoryValid = false;   // history is stale after a resize
    }

    // VERBATIM DrawTaa, guarded by the TAA-skipped reset (see class header). The old inline:
    //   if (taaOn) DrawTaa(); else taaHistoryValid = false;
    // becomes: Record runs in the native path; it does the real TAA only when ctx.TaaActive, else resets.
    public unsafe void Record(Dx12FrameContext ctx) {
        if (!ctx.TaaActive) { taaHistoryValid = false; return; }   // keep history fresh for when TAA turns back on

        Dx12OffscreenTarget target = ctx.Target;
        Dx12GBuffer gbuffer = ctx.GBuffer;
        int targetW = ctx.TargetW, targetH = ctx.TargetH;
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Dx12OffscreenTarget history = taaWriteB ? taaHistoryA : taaHistoryB;   // read from the OTHER buffer
        Dx12OffscreenTarget writeHist = taaWriteB ? taaHistoryB : taaHistoryA;

        taaCb.Write(new TaaConstants {
            Feedback = ctx.PostFX.TaaFeedback, ValidHistory = taaHistoryValid ? 1f : 0f,
            TexelSize = new Vector2(1f / targetW, 1f / targetH),
        });

        // PHASE-2 V3: skip the manual SceneColor head when derived barriers are active (the graph emitted
        // ctx.SceneColor.ColorToShaderResource() before Record). history is pass-private → always inline.
        if (!ctx.BarriersDerived) target.ColorToShaderResource();
        history.ColorToShaderResource();
        // Motion RT is already PixelShaderResource (gbuffer.ToShaderResource transitioned all colors).
        // C5: depth (t3) for closest-depth velocity dilation. The G-buffer depth is already a shader resource at
        // this point (the deferred/post chain reads it); bind it for the 3x3 closest-depth motion search.
        if (!ctx.BarriersDerived) gbuffer.DepthToShaderResource();
        taaSrvVisible.Reset();
        int b = taaSrvVisible.AllocateRange(4);
        dev.Device.CopyDescriptorsSimple(1, taaSrvVisible.Cpu(b + 0), target.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, taaSrvVisible.Cpu(b + 1), history.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, taaSrvVisible.Cpu(b + 2), gbuffer.ColorSrvCpu(Dx12GBuffer.MotionRtIndex), heapType);
        dev.Device.CopyDescriptorsSimple(1, taaSrvVisible.Cpu(b + 3), gbuffer.DepthSrvCpu, heapType);
        writeHist.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(taaRootSig); cl.SetPipelineState(taaPso);
            cl.SetDescriptorHeaps(taaSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, taaCb.Gpu);
            cl.SetGraphicsRootDescriptorTable(1, taaSrvVisible.Gpu(b));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
        writeHist.ColorToShaderResource();
        target.CopyColorFrom(writeHist);   // the resolved AA'd color becomes the scene color

        taaWriteB = !taaWriteB;
        taaHistoryValid = true;
        // taaFrame advances once per frame in BeginRender (shared by TAA + FSR jitter).
    }

    public void Dispose() {
        taaHistoryA?.Dispose(); taaHistoryB?.Dispose(); taaResolved?.Dispose();
        taaSrvVisible?.Dispose(); taaCb?.Dispose();   // Dx12FrameCb.Dispose unmaps + releases
        taaPso?.Dispose(); taaRootSig?.Dispose();
    }
}
