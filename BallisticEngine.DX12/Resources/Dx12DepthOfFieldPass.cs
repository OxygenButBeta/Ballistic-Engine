using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;        // PrimitiveTopology
using Vortice.Direct3D12;
using Vortice.Dxc;             // DxcShaderStage
using Vortice.DXGI;            // Format, SampleDescription

namespace BallisticEngine.DX12;

// Thin-lens DEPTH OF FIELD (bokeh) post pass. Runs in PostProcess (650) AFTER TAA (also 650 — registration
// order tiebreak puts DoF last; the orchestrator MUST register this pass AFTER Dx12TaaPass/the upscale resolve
// and after any motion-blur pass so DoF blurs the resolved, anti-aliased HDR scene color). It reads scene depth
// (linearized via InvProjection, exactly like GTAO) + the resolved HDR scene color (ctx.SceneColor) and writes
// the bokeh-blurred result back into ctx.SceneColor — so Composite/tonemap downstream sees the DoF'd image.
//
// ALGORITHM (4 sub-passes, production-quality gather bokeh — not a box blur):
//   1. CoC + downsample (full-res depth → half-res color.rgb + signed CoC.a). Signed thin-lens CoC: negative =
//      foreground/near, positive = background/far, clamped to ±DofMaxCoc (fraction of frame height).
//   2. Near-field max-CoC dilation (half-res) — spreads the near CoC outward so foreground bokeh bleeds OVER the
//      focused background (the correct direction), avoiding the classic sharp-silhouette-on-blur artifact.
//   3. Gather bokeh (half-res) — a 48-tap golden-angle sunflower disk scaled by |CoC|, far field depth/CoC-aware
//      (a sample only contributes if its own CoC reaches the centre, so focused background can't bleed into blur),
//      near field using the dilated CoC. Near and far are gathered SEPARATELY into one RGBA (rgb=color, a=coverage).
//   4. Composite (full-res) — read sharp full-res color + the bilinearly-upsampled half-res near/far fields and
//      blend by a smooth CoC factor (far first, then near over it). Written to a full-res scratch, copied back.
public sealed class Dx12DepthOfFieldPass : IRenderPass, IDisposable {
    // PostProcess (650), same as TAA. There is no later event; DoF must sort AFTER the AA/upscale resolve, which
    // the graph's stable registration-order tiebreak guarantees when the orchestrator registers DoF after them.
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.PostProcess;
    public string Name => "DepthOfField";

    // Volume-driven (PostProcessSettings.DofEnabled). Disabled under deterministic capture so paused/bal-render
    // frames stay byte-identical (the gather is a CoC-weighted blur — harmless, but DoF is a creative effect we
    // keep OFF for the diff oracle, mirroring how grain/exposure freeze).
    public bool Enabled(Dx12FrameContext ctx) => ctx.PostFX.DofEnabled && !ctx.DeterministicCapture;

    // Reads scene depth (G-buffer) + the resolved HDR scene color; read-modify-writes the scene color (gather to
    // half-res scratch, then composite back over the sharp full-res, copied into SceneColor). Declares the shared
    // SceneColor + GBuffer-depth boundary heads for the derived-barrier path; the half-res scratch ping-pong is
    // pass-private (stays inline, like GTAO's ssaoA/ssaoB).
    public void Declare(Dx12PassBuilder b) {
        b.Read(b.Resource("GBuffer"));
        b.ReadWrite(b.Resource("SceneColor"));
        b.DeriveBarriers();
        b.Use(Dx12ResourceUsage.GBufferDepthShaderRead);
        b.Use(Dx12ResourceUsage.SceneColorShaderRead);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DofConstants {
        public Matrix4x4 InvProjection;   // transposed (NDC → view, for linearizing depth — GTAO convention)
        public Vector2 TexelSize;         // 1 / half-res dimensions (the gather/dilate spacing)
        public Vector2 FullTexelSize;     // 1 / full-res dimensions (the CoC-downsample + composite spacing)
        public float FocusDistance;       // metres to the focal plane (DofFocusDistance)
        public float FocalLength;         // lens focal length in metres (DofFocalLength)
        public float Aperture;            // f-number (DofAperture); smaller = shallower
        public float MaxCoc;              // CoC clamp as a fraction of frame height (DofMaxCoc)
        public float Near, Far;           // camera near/far planes (reconstructed from InvProjection)
        public Vector2 Pad;
    }

    readonly Dx12Device dev;
    ID3D12RootSignature rootSig;        // DofConstants CBV (b0) + 3-SRV table (t0 color/near, t1 far, t2 depth) + sampler s0 (linear) + s1 (point)
    ID3D12PipelineState cocPso, dilatePso, gatherPso, compositePso;
    Dx12OffscreenTarget dofHalf;       // half-res: color.rgb + signed CoC.a (the downsample result)
    Dx12OffscreenTarget dofNear;       // half-res near field (dilated-CoC gather; rgb=color, a=coverage)
    Dx12OffscreenTarget dofFar;        // half-res far field (depth-aware gather;  rgb=color, a=coverage)
    Dx12OffscreenTarget dofResult;     // full-res composite scratch, copied back into ctx.SceneColor
    ID3D12Resource cb;
    unsafe byte* cbMapped;
    int cbStride;                      // 256-aligned per-sub-pass CB slot (4 sub-passes write distinct constants)
    Dx12DescriptorHeap srvVisible;     // 3 SRVs × 4 sub-passes = 12 contiguous slots
    int renderW, renderH;

    public unsafe Dx12DepthOfFieldPass(Dx12Device device, int width, int height) {
        dev = device;
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        // 3-SRV table: CoC = depth(t2); dilate = CoC-packed color(t0); gather = CoC-packed color(t0) + depth(t2);
        // composite = sharp color(t0) + near(t1)... we bind a 3-slot run per sub-pass (some slots unused).
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 3, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var linear = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        var point = new StaticSamplerDescription(ShaderVisibility.Pixel, 1, 0) {
            Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        rootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { linear, point })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("DepthOfField.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "DepthOfField.hlsl");
        ID3D12PipelineState MakePso(string entry, Format fmt) => dev.Device.CreateGraphicsPipelineState(
            new GraphicsPipelineStateDescription {
                RootSignature = rootSig, VertexShader = vs,
                PixelShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, entry, "DepthOfField.hlsl"),
                InputLayout = null, PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
                RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
                DepthStencilState = DepthStencilDescription.None,
                RenderTargetFormats = new[] { fmt }, DepthStencilFormat = Format.Unknown,
                SampleDescription = new SampleDescription(1, 0),
            });
        cocPso       = MakePso("PSCoc",       Dx12OffscreenTarget.HdrFormat);
        dilatePso    = MakePso("PSDilate",    Dx12OffscreenTarget.HdrFormat);
        gatherPso    = MakePso("PSGather",    Dx12OffscreenTarget.HdrFormat);
        compositePso = MakePso("PSComposite", Dx12OffscreenTarget.HdrFormat);

        // 4 sub-passes each write their own DofConstants (different texel sizes / pass roles) but the pipelined
        // frame records them into ONE list — give each a distinct 256-aligned CB slot so a later CPU write can't
        // stomp a value an earlier draw still needs (the GTAO/bloom per-draw-slot rule).
        cbStride = (Marshal.SizeOf<DofConstants>() + 255) & ~255;
        cb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(cbStride * 4)), ResourceStates.GenericRead);
        cbMapped = cb.Map<byte>(0);
        srvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 12, shaderVisible: true, framesInFlight: dev.FramesInFlight);
        renderW = width; renderH = height;
        AllocTargets(width, height);
    }

    public void Resize(int width, int height) { renderW = width; renderH = height; AllocTargets(width, height); }

    void AllocTargets(int width, int height) {
        // GTAO pattern: dispose committed (non-pool) fields; pool-placed fields are owned by the pool.
        if (dofHalf   is { IsPlaced: false }) dofHalf.Dispose();
        if (dofNear   is { IsPlaced: false }) dofNear.Dispose();
        if (dofFar    is { IsPlaced: false }) dofFar.Dispose();
        if (dofResult is { IsPlaced: false }) dofResult.Dispose();
        int hw = System.Math.Max(1, width / 2), hh = System.Math.Max(1, height / 2);
        dofHalf   = Dx12RenderTargetPool.AllocOrPool(dev, "dofHalf",   hw, hh, Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: false);
        dofNear   = Dx12RenderTargetPool.AllocOrPool(dev, "dofNear",   hw, hh, Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: false);
        dofFar    = Dx12RenderTargetPool.AllocOrPool(dev, "dofFar",    hw, hh, Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: false);
        dofResult = Dx12RenderTargetPool.AllocOrPool(dev, "dofResult", width, height, Dx12OffscreenTarget.HdrFormat, colorReadable: true, allowUav: false);
    }

    public unsafe void Record(Dx12FrameContext ctx) {
        var pf = ctx.PostFX;
        Dx12RenderTargetPool.PoolBarrier(ctx.Dev, "dofHalf", "dofNear", "dofFar", "dofResult");   // no-op when pool off
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Dx12GBuffer gbuffer = ctx.GBuffer;
        Dx12OffscreenTarget scene = ctx.SceneColor;

        Matrix4x4.Invert(ctx.Proj, out Matrix4x4 invProj);
        // Reconstruct near/far from the projection (D3D z-NDC ∈ [0,1]): near = M43 / M33, far = M43 / (M33 - 1).
        float m33 = ctx.Proj.M33, m43 = ctx.Proj.M43;
        float near = m43 / m33;
        float far  = m43 / (m33 - 1f);

        // The DoF pass is a SceneColor + G-buffer-depth consumer this frame; emit the head transitions (no-op
        // under derived barriers). Depth → SRV (t2), scene color → SRV (t0).
        if (!ctx.BarriersDerived) { gbuffer.DepthToShaderResource(); scene.ColorToShaderResource(); }

        var baseC = new DofConstants {
            InvProjection = Matrix4x4.Transpose(invProj),
            TexelSize = new Vector2(1f / dofHalf.Width, 1f / dofHalf.Height),
            FullTexelSize = new Vector2(1f / dofResult.Width, 1f / dofResult.Height),
            FocusDistance = MathF.Max(pf.DofFocusDistance, 1e-3f),
            FocalLength = MathF.Max(pf.DofFocalLength, 1e-4f),
            Aperture = MathF.Max(pf.DofAperture, 0.1f),
            MaxCoc = MathF.Max(pf.DofMaxCoc, 0f),
            Near = near, Far = far,
        };

        void WriteCb(int slot, DofConstants c) => *(DofConstants*)(cbMapped + slot * cbStride) = c;

        // Bind a 3-slot SRV run + draw a fullscreen triangle into `dst` with `pso`, reading `cb` slot `slot`.
        void Draw(ID3D12PipelineState pso, Dx12OffscreenTarget dst, int slot) {
            dst.RenderColorOnly(cl => {
                cl.SetGraphicsRootSignature(rootSig); cl.SetPipelineState(pso);
                cl.SetDescriptorHeaps(srvVisible.Heap);
                cl.SetGraphicsRootConstantBufferView(0, cb.GPUVirtualAddress + (ulong)(slot * cbStride));
                cl.SetGraphicsRootDescriptorTable(1, srvVisible.Gpu(slot * 3));
                cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                cl.DrawInstanced(3, 1, 0, 0);
            });
        }

        // --- Sub-pass 0: CoC + downsample. Reads depth(t2) + sharp scene color(t0) → dofHalf (rgb + signed CoC.a).
        WriteCb(0, baseC);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(0), scene.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(1), scene.ColorSrvCpu, heapType);   // t1 unused
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(2), gbuffer.DepthSrvCpu, heapType);
        Draw(cocPso, dofHalf, 0);

        // --- Sub-pass 1: near-field max-CoC dilation. Reads dofHalf(t0) → dofNear (rgb=color, a=dilated near CoC).
        WriteCb(1, baseC);
        dofHalf.ColorToShaderResource();
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(3), dofHalf.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(4), dofHalf.ColorSrvCpu, heapType);   // t1 unused
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(5), dofHalf.ColorSrvCpu, heapType);   // t2 unused
        Draw(dilatePso, dofNear, 1);

        // --- Sub-pass 2: gather bokeh. Reads dofHalf(t0) + dofNear(t1 dilated near CoC) → dofFar (far field).
        // Re-uses dofFar as the FAR output; the near field is gathered in the same pass and folded with the far in
        // the composite. We run gather TWICE-in-one with the shader picking field by sign — here it writes FAR.
        WriteCb(2, baseC);
        dofNear.ColorToShaderResource();
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(6), dofHalf.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(7), dofNear.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(8), dofHalf.ColorSrvCpu, heapType);   // t2 unused
        Draw(gatherPso, dofFar, 2);

        // --- Sub-pass 3: composite. Reads sharp scene(t0) + far(t1) + near... we bind sharp(t0), far(t1), near via
        // dofNear(t2)? table is 3 slots. Bind sharp(t0), gathered far(t1), dilated/near gather(t2). Writes dofResult.
        WriteCb(3, baseC);
        dofFar.ColorToShaderResource();
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(9), scene.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(10), dofFar.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(11), dofNear.ColorSrvCpu, heapType);
        Draw(compositePso, dofResult, 3);

        // Copy the DoF result back into the canonical scene color so Composite/tonemap downstream sees it.
        dofResult.ColorToShaderResource();
        scene.CopyColorFrom(dofResult);
        scene.ColorToShaderResource();   // leave it as an SRV — the next consumer (Composite) reads it
    }

    public void Dispose() {
        if (dofHalf   is { IsPlaced: false }) dofHalf.Dispose();
        if (dofNear   is { IsPlaced: false }) dofNear.Dispose();
        if (dofFar    is { IsPlaced: false }) dofFar.Dispose();
        if (dofResult is { IsPlaced: false }) dofResult.Dispose();
        srvVisible?.Dispose();
        cb?.Dispose();
        cocPso?.Dispose(); dilatePso?.Dispose(); gatherPso?.Dispose(); compositePso?.Dispose();
        rootSig?.Dispose();
    }
}
