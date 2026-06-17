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

    [StructLayout(LayoutKind.Sequential)]
    struct TaaConstants { public float Feedback; public float ValidHistory; public Vector2 TexelSize; }

    readonly Dx12Device dev;
    ID3D12RootSignature taaRootSig;     // TaaConstants CBV(b0) + 3-SRV table(current/history/motion) + sampler
    ID3D12PipelineState taaPso;
    ID3D12Resource taaCb;
    unsafe byte* taaCbMapped;
    Dx12OffscreenTarget taaHistoryA, taaHistoryB;   // ping-pong accumulated HDR history
    Dx12OffscreenTarget taaResolved;                // this frame's TAA output (→ history + copied to target)
    Dx12DescriptorHeap taaSrvVisible;   // 3 SRVs per frame
    bool taaWriteB;                     // ping-pong toggle
    bool taaHistoryValid;

    // VERBATIM BuildTaa + the trailing AllocTaaTargets.
    public unsafe Dx12TaaPass(Dx12Device device, int width, int height) {
        dev = device;
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 3, baseShaderRegister: 0);
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

        int cbSize = (Marshal.SizeOf<TaaConstants>() + 255) & ~255;
        taaCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        taaCbMapped = taaCb.Map<byte>(0);
        taaSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 3, shaderVisible: true, framesInFlight: dev.FramesInFlight);
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

        *(TaaConstants*)taaCbMapped = new TaaConstants {
            Feedback = ctx.PostFX.TaaFeedback, ValidHistory = taaHistoryValid ? 1f : 0f,
            TexelSize = new Vector2(1f / targetW, 1f / targetH),
        };

        target.ColorToShaderResource();
        history.ColorToShaderResource();
        // Motion RT is already PixelShaderResource (gbuffer.ToShaderResource transitioned all colors).
        taaSrvVisible.Reset();
        int b = taaSrvVisible.AllocateRange(3);
        dev.Device.CopyDescriptorsSimple(1, taaSrvVisible.Cpu(b + 0), target.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, taaSrvVisible.Cpu(b + 1), history.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, taaSrvVisible.Cpu(b + 2), gbuffer.ColorSrvCpu(Dx12GBuffer.MotionRtIndex), heapType);
        writeHist.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(taaRootSig); cl.SetPipelineState(taaPso);
            cl.SetDescriptorHeaps(taaSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, taaCb.GPUVirtualAddress);
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
        taaSrvVisible?.Dispose(); taaCb?.Dispose();
        taaPso?.Dispose(); taaRootSig?.Dispose();
    }
}
