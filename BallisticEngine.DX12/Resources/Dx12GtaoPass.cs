using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;        // PrimitiveTopology
using Vortice.Direct3D12;
using Vortice.Dxc;             // DxcShaderStage
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// Ground-Truth Ambient Occlusion (GTAO, Jimenez 2016) from the G-buffer. Replaces the old ported HBAO
// (Dx12SsaoPass). It reads scene depth (view-pos), the G-buffer world normal, and albedo (for the
// albedo-aware multi-bounce remap), and WRITES a blurred AO target in `gtaoA`.
//
// ARCHITECTURE (the rework's core): this pass runs at AfterGBuffer (200), BEFORE deferred lighting
// (OpaqueLighting 300) — NOT in PostProcess like the old SSAO. That ordering lets the deferred pass sample
// the AO (ctx.AoResult) and multiply it into the IBL AMBIENT term only, the physically-correct layer. The
// old HBAO ran post-deferred and could only post-multiply the whole HDR colour (darkening direct light too).
//
// All non-constant parameters come from the AmbientOcclusion volume via ctx.PostFX (SSAORadius / SSAOIntensity
// / SSAOPower / SSAOThickness / SSAOMultiBounce / SSAOQuality / SSAOResolution) — nothing is hardcoded in the
// shader anymore. Quality drives the slice/step counts; Resolution drives the render fraction.
public sealed class Dx12GtaoPass : IRenderPass, IDisposable {
    // Runs right after the G-buffer is filled, before deferred lighting consumes it. The G-buffer is already
    // an SRV at this point (GTAO is the first consumer); deferred's own head transition is then a no-op.
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.AfterGBuffer;
    public string Name => "GTAO";

    // Gated by BOTH the env-door (debug A/B) AND the volume's enable (so the AmbientOcclusion volume now
    // genuinely turns AO off, not just the env var).
    public bool Enabled(Dx12FrameContext ctx) => ctx.Doors.Ssao && ctx.PostFX.SSAOEnabled;

    // Reads the G-buffer (depth + world normal + albedo), writes its own AO target. Disjoint from SceneColor,
    // so it only orders against the G-buffer producers (geometry) and consumers (deferred).
    public void Declare(Dx12PassBuilder b) {
        b.Read(b.Resource("GBuffer"));
        b.Write(b.Resource("Ao"));
        b.DeriveBarriers();
        b.Use(Dx12ResourceUsage.GBufferDepthShaderRead);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct GtaoConstants {
        public Matrix4x4 Projection; public Matrix4x4 InvProjection; public Matrix4x4 View;
        public float Radius; public float Intensity; public float Power; public float Thickness;
        public Vector2 TexelSize;
        public float MultiBounce; public float SliceCount; public float StepCount; public float FrameIndex;
        public Vector2 Pad;
    }

    readonly Dx12Device dev;
    ID3D12RootSignature rootSig;        // GtaoConstants CBV (b0) + 3-SRV table (t0 depth/AO, t1 normal, t2 albedo) + sampler
    ID3D12PipelineState gtaoPso, blurHPso, blurVPso;
    Dx12OffscreenTarget gtaoA, gtaoB;   // R8 ping-pong at the chosen AO resolution
    ID3D12Resource cb;
    unsafe byte* cbMapped;
    Dx12DescriptorHeap srvVisible;      // 3 SRVs × 3 sub-passes = 9 contiguous slots
    int frameCounter;                   // animates the per-pixel rotation (frozen under deterministic capture)
    AoResolution allocatedRes = AoResolution.Half;
    int renderW, renderH;

    // The blurred AO the deferred lighting pass samples (gtaoA after the V blur). Exposed via ctx.AoResult.
    public CpuDescriptorHandle ResultSrvCpu => gtaoA.ColorSrvCpu;

    public unsafe Dx12GtaoPass(Dx12Device device, int width, int height) {
        dev = device;
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        // 3-SRV table: main pass = depth(t0) + normal(t1) + albedo(t2); blur passes = AO(t0) (t1/t2 unused).
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 3, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var samp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        rootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { samp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("Gtao.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "Gtao.hlsl");
        ID3D12PipelineState MakePso(string entry) => dev.Device.CreateGraphicsPipelineState(
            new GraphicsPipelineStateDescription {
                RootSignature = rootSig, VertexShader = vs,
                PixelShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, entry, "Gtao.hlsl"),
                InputLayout = null, PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
                RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
                DepthStencilState = DepthStencilDescription.None,
                RenderTargetFormats = new[] { Format.R8_UNorm }, DepthStencilFormat = Format.Unknown,
                SampleDescription = new SampleDescription(1, 0),
            });
        gtaoPso = MakePso("PSMain");
        blurHPso = MakePso("PSBlurH");
        blurVPso = MakePso("PSBlurV");

        int cbSize = (Marshal.SizeOf<GtaoConstants>() + 255) & ~255;
        cb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        cbMapped = cb.Map<byte>(0);
        // Main pass = 3-SRV run (depth+normal+albedo); each blur = 3-SRV run (AO at t0). 3 runs × 3 = 9 slots.
        srvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 9, shaderVisible: true, framesInFlight: dev.FramesInFlight);
        renderW = width; renderH = height;
        AllocTargets(width, height);
    }

    public void Resize(int width, int height) { renderW = width; renderH = height; AllocTargets(width, height); }

    // Divisor for the chosen AO resolution (Full=1, Half=2, Quarter=4).
    static int ResDivisor(AoResolution r) => r switch {
        AoResolution.Full => 1, AoResolution.Quarter => 4, _ => 2,
    };

    void AllocTargets(int width, int height) {
        // ssaoA/ssaoB pattern: dispose committed (non-pool) fields; pool-placed fields are owned by the pool.
        if (gtaoA is { IsPlaced: false }) gtaoA.Dispose();
        if (gtaoB is { IsPlaced: false }) gtaoB.Dispose();
        int div = ResDivisor(allocatedRes);
        int w = System.Math.Max(1, width / div), h = System.Math.Max(1, height / div);
        gtaoA = Dx12RenderTargetPool.AllocOrPool(dev, "gtaoA", w, h, Format.R8_UNorm, colorReadable: true, allowUav: false);
        gtaoB = Dx12RenderTargetPool.AllocOrPool(dev, "gtaoB", w, h, Format.R8_UNorm, colorReadable: true, allowUav: false);
    }

    // Maps the quality preset to (slices, stepsPerSide). The shader [unroll]s up to MAX_SLICES(6)/MAX_STEPS(12).
    static (int slices, int steps) QualityToCounts(AoQuality q) => q switch {
        AoQuality.Low    => (2, 4),
        AoQuality.High   => (4, 8),
        AoQuality.Ultra  => (6, 12),
        _                => (3, 6),   // Medium
    };

    public unsafe void Record(Dx12FrameContext ctx) {
        var pf = ctx.PostFX;
        // Resolution dropdown can change at runtime (volume edit) — re-alloc the ping-pong if it did.
        if (pf.SSAOResolution != allocatedRes) {
            allocatedRes = pf.SSAOResolution;
            AllocTargets(renderW, renderH);
        }

        Dx12RenderTargetPool.PoolBarrier(ctx.Dev, "gtaoA", "gtaoB");
        Dx12GBuffer gbuffer = ctx.GBuffer;
        Matrix4x4 view = ctx.View, proj = ctx.Proj;
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Matrix4x4.Invert(proj, out Matrix4x4 invProj);

        // GTAO is the FIRST G-buffer-as-SRV consumer this frame; emit the head transition (no-op under derived).
        if (!ctx.BarriersDerived) gbuffer.DepthToShaderResource();

        var (slices, steps) = QualityToCounts(pf.SSAOQuality);
        // Frozen frame index under deterministic capture so f24 == f240 byte-identically.
        float frameIdx = ctx.DeterministicCapture ? 0f : frameCounter;
        *(GtaoConstants*)cbMapped = new GtaoConstants {
            Projection = Matrix4x4.Transpose(proj), InvProjection = Matrix4x4.Transpose(invProj),
            View = Matrix4x4.Transpose(view),
            Radius = pf.SSAORadius, Intensity = pf.SSAOIntensity, Power = pf.SSAOPower, Thickness = pf.SSAOThickness,
            TexelSize = new Vector2(1f / gtaoA.Width, 1f / gtaoA.Height),
            MultiBounce = pf.SSAOMultiBounce ? 1f : 0f, SliceCount = slices, StepCount = steps, FrameIndex = frameIdx,
        };

        // Main AO pass: depth(t0) + world normal(t1) + albedo(t2) -> gtaoA. Slots 0,1,2.
        // G-buffer layout: ColorSrvCpu(0) = albedo, (1) = world normal, (2) = metallic/rough/ao.
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(0), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(1), gbuffer.ColorSrvCpu(1), heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(2), gbuffer.ColorSrvCpu(0), heapType);
        gtaoA.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(rootSig); cl.SetPipelineState(gtaoPso);
            cl.SetDescriptorHeaps(srvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, cb.GPUVirtualAddress);
            cl.SetGraphicsRootDescriptorTable(1, srvVisible.Gpu(0));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });

        // Bilateral blur H (gtaoA->gtaoB), V (gtaoB->gtaoA). Each binds a 3-slot run (AO at t0; t1/t2 unused).
        void Blur(ID3D12PipelineState pso, Dx12OffscreenTarget src, Dx12OffscreenTarget dst, int srvSlot) {
            src.ColorToShaderResource();
            dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(srvSlot), src.ColorSrvCpu, heapType);
            dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(srvSlot + 1), src.ColorSrvCpu, heapType);
            dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(srvSlot + 2), src.ColorSrvCpu, heapType);
            dst.RenderColorOnly(cl => {
                cl.SetGraphicsRootSignature(rootSig); cl.SetPipelineState(pso);
                cl.SetDescriptorHeaps(srvVisible.Heap);
                cl.SetGraphicsRootConstantBufferView(0, cb.GPUVirtualAddress);
                cl.SetGraphicsRootDescriptorTable(1, srvVisible.Gpu(srvSlot));
                cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                cl.DrawInstanced(3, 1, 0, 0);
            });
        }
        Blur(blurHPso, gtaoA, gtaoB, 3);
        Blur(blurVPso, gtaoB, gtaoA, 6);
        gtaoA.ColorToShaderResource();   // deferred lighting samples it as an SRV next (event 300)
        frameCounter++;
    }

    public void Dispose() {
        gtaoA?.Dispose(); gtaoB?.Dispose();
        srvVisible?.Dispose();
        cb?.Dispose();
        gtaoPso?.Dispose(); blurHPso?.Dispose(); blurVPso?.Dispose();
        rootSig?.Dispose();
    }
}
