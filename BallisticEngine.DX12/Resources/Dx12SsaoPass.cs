using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;        // PrimitiveTopology
using Vortice.Direct3D12;
using Vortice.Dxc;             // DxcShaderStage
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// HBAO from the G-buffer (scene depth for view-pos + world normal, both already SRVs from the deferred
// pass) → blurred half-res AO in `result` (ssaoA). The composite multiplies this AO in. The FIRST leaf-post
// pass converted from DX12HDRenderer's inline DrawSsao — the template the rest of the leaf-post passes copy
// (chunk 4 of the pass-graph migration).
//
// VERBATIM MOVE: the body of BuildSsao/AllocSsaoTargets/DrawSsao is copied unchanged, only re-rooted onto
// `ctx`/this pass's own fields. No logic change → eyeball-unchanged + zero NEW GBV (a MOVE-only commit).
//
// Decision 4 / R2: the head resource transition (gbuffer.DepthToShaderResource) lives at the TOP of Record —
// the pass emits its OWN idempotent head transition, never relying on an upstream pass. SSAO runs in the
// PostProcess group (just before TAA/FSR/composite in the inline frame today).
//
// Cross-pass output: the still-inline composite (chunk 7) reads this pass's `result` (ssaoA) — exposed via
// ResultSrvCpu so the orchestrator can bind it until composite itself converts.
public sealed class Dx12SsaoPass : IRenderPass, IDisposable {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.PostProcess;
    public string Name => "SSAO";

    // The VERBATIM outer-if predicate: `bool ssaoOn = doors.Ssao;` then `if (ssaoOn) DrawSsao(...)`.
    public bool Enabled(Dx12FrameContext ctx) => ctx.Doors.Ssao;

    // PHASE-2 V1: reads the G-buffer (depth + world-normal) and WRITES its own half-res AO target (the "Ssao"
    // handle, which the Composite pass reads via ctx.SsaoResult). Does NOT touch SceneColor — that's why SSAO
    // can sit anywhere in PostProcess relative to TAA/FSR (disjoint resources, the chunk-4 reorder finding).
    public void Declare(Dx12PassBuilder b) {
        b.Read(b.Resource("GBuffer"));
        b.Write(b.Resource("Ssao"));
        // PHASE-2 V3 (chunk 14): opt into DERIVED boundary barriers. SSAO's ONE shared-resource head transition is
        // `gbuffer.DepthToShaderResource()` — declare it as a usage so the graph derives + emits it before Record
        // (under BALLISTIC_DX12_GRAPH_BARRIERS=1). The pass-private ssaoA/ssaoB ping-pong transitions are NOT
        // boundary transitions → they stay inline in Record. SSAO is the first migrated pass (the template).
        b.DeriveBarriers();
        b.Use(Dx12ResourceUsage.GBufferDepthShaderRead);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SsaoConstants {
        public Matrix4x4 Projection; public Matrix4x4 InvProjection; public Matrix4x4 View;
        public float Radius; public float Intensity; public Vector2 TexelSize;
    }

    readonly Dx12Device dev;
    ID3D12RootSignature ssaoRootSig;    // SsaoConstants CBV (b0) + 1 SRV (t0: depth, then AO for blur) + sampler
    ID3D12PipelineState ssaoPso, ssaoBlurHPso, ssaoBlurVPso;
    Dx12OffscreenTarget ssaoA, ssaoB;   // half-res R8 ping-pong
    ID3D12Resource ssaoCb;
    unsafe byte* ssaoCbMapped;
    Dx12DescriptorHeap ssaoSrvVisible;  // depth/AO source per sub-pass (3 slots × 2 = 6)

    // The blurred half-res AO the composite samples (ssaoA after the V blur). Exposed so the still-inline
    // composite can bind it until composite itself converts to a pass (chunk 7).
    public CpuDescriptorHandle ResultSrvCpu => ssaoA.ColorSrvCpu;

    // VERBATIM BuildSsao + the trailing AllocSsaoTargets. Allocates rootsig/PSOs/CB/heap once, then sizes the
    // ping-pong targets to (w, h) = the render resolution.
    public unsafe Dx12SsaoPass(Dx12Device device, int width, int height) {
        dev = device;
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        // 2-SRV table: main pass = depth(t0) + G-buffer world normal(t1); blur passes = AO(t0).
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 2, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var samp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        ssaoRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { samp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("Ssao.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "Ssao.hlsl");
        ID3D12PipelineState MakePso(string entry) => dev.Device.CreateGraphicsPipelineState(
            new GraphicsPipelineStateDescription {
                RootSignature = ssaoRootSig, VertexShader = vs,
                PixelShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, entry, "Ssao.hlsl"),
                InputLayout = null, PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
                RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
                DepthStencilState = DepthStencilDescription.None,
                RenderTargetFormats = new[] { Format.R8_UNorm }, DepthStencilFormat = Format.Unknown,
                SampleDescription = new SampleDescription(1, 0),
            });
        ssaoPso = MakePso("PSMain");
        ssaoBlurHPso = MakePso("PSBlurH");
        ssaoBlurVPso = MakePso("PSBlurV");

        int cbSize = (Marshal.SizeOf<SsaoConstants>() + 255) & ~255;
        ssaoCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        ssaoCbMapped = ssaoCb.Map<byte>(0);
        // Main pass binds a 2-SRV run (depth+normal); each blur binds a 2-SRV run (AO at t0, t1 unused).
        // 3 runs × 2 = 6 contiguous slots.
        ssaoSrvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 6, shaderVisible: true, framesInFlight: dev.FramesInFlight);
        AllocTargets(width, height);
    }

    // VERBATIM AllocSsaoTargets. The graph fans Resize out in registration order; SSAO's slot in the original
    // AllocateResolutionTargets sequence is 2nd (bloom→ssao→ssr→ssgi→taa→rtShadowMask→fsr, R5) — registration
    // order must reproduce that as the rest of the leaf-post passes convert.
    public void Resize(int width, int height) => AllocTargets(width, height);

    void AllocTargets(int width, int height) {
        // V2: AllocOrPool returns a COMMITTED target (byte-identical) when no pool is active, else a PLACED target
        // aliased onto the pool heap. ssaoA/ssaoB are audit-passed transients (each is the full-overwrite DST of a
        // full-screen draw before it is read) — safe to alias. Dispose the current field UNLESS it's a pool-placed
        // target (the pool re-acquire disposes its own Live; double-disposing it is the bug). A committed field
        // (no pool, or the pre-pool ctor allocation about to be replaced by a placed one) is disposed here.
        if (ssaoA is { IsPlaced: false }) ssaoA.Dispose();
        if (ssaoB is { IsPlaced: false }) ssaoB.Dispose();
        int w = System.Math.Max(1, width / 2), h = System.Math.Max(1, height / 2);
        ssaoA = Dx12RenderTargetPool.AllocOrPool(dev, "ssaoA", w, h, Format.R8_UNorm, colorReadable: true, allowUav: false);
        ssaoB = Dx12RenderTargetPool.AllocOrPool(dev, "ssaoB", w, h, Format.R8_UNorm, colorReadable: true, allowUav: false);
    }

    // VERBATIM DrawSsao. HBAO from the G-buffer (scene depth for view-pos + world normal, both already SRVs
    // from the deferred pass) → blurred half-res AO in ssaoA. The real surface normal comes straight from the
    // G-buffer (sharper, silhouette-correct); View transforms the world normal into view space for the march.
    public unsafe void Record(Dx12FrameContext ctx) {
        Dx12RenderTargetPool.PoolBarrier(ctx.Dev, "ssaoA", "ssaoB");   // V2: aliasing barrier + discard the produced placed targets (no-op when pool off)
        Dx12GBuffer gbuffer = ctx.GBuffer;
        Matrix4x4 view = ctx.View, proj = ctx.Proj;
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Matrix4x4.Invert(proj, out Matrix4x4 invProj);
        // Head transition (R2): emit our own; no-op if fog already moved it. PHASE-2 V3 (chunk 14): when derived
        // barriers are active the GRAPH already emitted this (deriver.Emit before Record), so SKIP the manual one
        // — emit the derived set ONLY (plan §V3: not manual+derived stacked, which would muddy the GBV sequence).
        if (!ctx.BarriersDerived) gbuffer.DepthToShaderResource();
        *(SsaoConstants*)ssaoCbMapped = new SsaoConstants {
            Projection = Matrix4x4.Transpose(proj), InvProjection = Matrix4x4.Transpose(invProj),
            View = Matrix4x4.Transpose(view),
            Radius = 0.5f, Intensity = 1.0f, TexelSize = new Vector2(1f / ssaoA.Width, 1f / ssaoA.Height),
        };
        // Main AO pass: depth(t0) + G-buffer world normal(t1) → ssaoA. Uses slots 0,1.
        dev.Device.CopyDescriptorsSimple(1, ssaoSrvVisible.Cpu(0), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, ssaoSrvVisible.Cpu(1), gbuffer.ColorSrvCpu(1), heapType);
        ssaoA.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(ssaoRootSig); cl.SetPipelineState(ssaoPso);
            cl.SetDescriptorHeaps(ssaoSrvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, ssaoCb.GPUVirtualAddress);
            cl.SetGraphicsRootDescriptorTable(1, ssaoSrvVisible.Gpu(0));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
        // Blur H (ssaoA→ssaoB), Blur V (ssaoB→ssaoA). Each binds a 2-slot run (AO at t0; t1 unused but
        // copied so the descriptor is valid). Runs at slots 2 and 4.
        void Blur(ID3D12PipelineState pso, Dx12OffscreenTarget src, Dx12OffscreenTarget dst, int srvSlot) {
            src.ColorToShaderResource();
            dev.Device.CopyDescriptorsSimple(1, ssaoSrvVisible.Cpu(srvSlot), src.ColorSrvCpu, heapType);
            dev.Device.CopyDescriptorsSimple(1, ssaoSrvVisible.Cpu(srvSlot + 1), src.ColorSrvCpu, heapType);
            dst.RenderColorOnly(cl => {
                cl.SetGraphicsRootSignature(ssaoRootSig); cl.SetPipelineState(pso);
                cl.SetDescriptorHeaps(ssaoSrvVisible.Heap);
                cl.SetGraphicsRootConstantBufferView(0, ssaoCb.GPUVirtualAddress);
                cl.SetGraphicsRootDescriptorTable(1, ssaoSrvVisible.Gpu(srvSlot));
                cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                cl.DrawInstanced(3, 1, 0, 0);
            });
        }
        Blur(ssaoBlurHPso, ssaoA, ssaoB, 2);
        Blur(ssaoBlurVPso, ssaoB, ssaoA, 4);
        ssaoA.ColorToShaderResource();
    }

    public void Dispose() {
        ssaoA?.Dispose(); ssaoB?.Dispose();
        ssaoSrvVisible?.Dispose();
        ssaoCb?.Dispose();
        ssaoPso?.Dispose(); ssaoBlurHPso?.Dispose(); ssaoBlurVPso?.Dispose();
        ssaoRootSig?.Dispose();
    }
}
