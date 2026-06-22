using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

public sealed class Dx12GtaoPass : IRenderPass, IDisposable {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.AfterGBuffer;
    public string Name => "GTAO";

    // FAZ -1d-FINAL — when render-graph v2 owns the whole frame (v1 bypassed) it drives GTAO itself; the v1
    // graph then SKIPS this pass via RgV2OwnsGtao. Door off (and door-on-while-plumbing) => RgV2OwnsGtao is
    // false => Enabled unchanged. See Dx12FrameContext.RgV2OwnsGtao.
    public bool Enabled(Dx12FrameContext ctx) => ctx.Doors.Ssao && ctx.PostFX.SSAOEnabled && !ctx.RgV2OwnsGtao;

    // FAZ -1d-FINAL — render-graph v2 entry point (mirrors Dx12ReflectionsPass.RecordV2). v2 imports GBuffer
    // (depth read) + the Ao target (write), declares the access, then calls this to run the SAME record body
    // (byte-identical to the v1 path). Under v2 the v1 barrier deriver is bypassed (pass skipped in v1) AND v2
    // emits no barrier for the imports (equal states by design), so the body MUST own its input transition.
    // The Record body guards its only at-entry input transition — `gbuffer.DepthToShaderResource()` — behind
    // `!ctx.BarriersDerived` (matches Declare's GBufferDepthShaderRead). It also samples gbuffer color(0)/(1)
    // as SRVs, but (like the Reflections SSR body) assumes those are already shader-read from the upstream
    // G-buffer pass — only the depth is force-transitioned here. The Ao targets (gtaoA/gtaoB) are written via
    // RenderColorOnly / PoolBarrier (their own RT transitions inside Record), so no force is needed for them.
    public void RecordV2(Dx12FrameContext ctx) {
        ctx.GBuffer.DepthToShaderResource();
        Record(ctx);
    }

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
    ID3D12RootSignature rootSig;
    ID3D12PipelineState gtaoPso, blurHPso, blurVPso;
    Dx12OffscreenTarget gtaoA, gtaoB;
    ID3D12Resource cb;
    unsafe byte* cbMapped;
    Dx12DescriptorHeap srvVisible;
    int frameCounter;
    AoResolution allocatedRes = AoResolution.Half;
    int renderW, renderH;

    public CpuDescriptorHandle ResultSrvCpu => gtaoA.ColorSrvCpu;

    public Dx12OffscreenTarget AoTarget => gtaoA;

    public unsafe Dx12GtaoPass(Dx12Device device, int width, int height) {
        dev = device;
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
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
        srvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 9, shaderVisible: true, framesInFlight: dev.FramesInFlight);
        renderW = width; renderH = height;
        AllocTargets(width, height);
    }

    public void Resize(int width, int height) { renderW = width; renderH = height; AllocTargets(width, height); }

    static int ResDivisor(AoResolution r) => r switch {
        AoResolution.Full => 1, AoResolution.Quarter => 4, _ => 2,
    };

    void AllocTargets(int width, int height) {
        if (gtaoA is { IsPlaced: false }) dev.DeferredRelease(gtaoA);
        if (gtaoB is { IsPlaced: false }) dev.DeferredRelease(gtaoB);
        int div = ResDivisor(allocatedRes);
        int w = System.Math.Max(1, width / div), h = System.Math.Max(1, height / div);
        gtaoA = Dx12RenderTargetPool.AllocOrPool(dev, "gtaoA", w, h, Format.R8_UNorm, colorReadable: true, allowUav: false);
        gtaoB = Dx12RenderTargetPool.AllocOrPool(dev, "gtaoB", w, h, Format.R8_UNorm, colorReadable: true, allowUav: false);
    }

    static (int slices, int steps) QualityToCounts(AoQuality q) => q switch {
        AoQuality.Low    => (2, 4),
        AoQuality.High   => (4, 8),
        AoQuality.Ultra  => (6, 12),
        _                => (3, 6),
    };

    public unsafe void Record(Dx12FrameContext ctx) {
        var pf = ctx.PostFX;
        if (pf.SSAOResolution != allocatedRes) {
            allocatedRes = pf.SSAOResolution;
            AllocTargets(renderW, renderH);
        }

        Dx12RenderTargetPool.PoolBarrier(ctx.Dev, "gtaoA", "gtaoB");
        Dx12GBuffer gbuffer = ctx.GBuffer;
        Matrix4x4 view = ctx.View, proj = ctx.Proj;
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Matrix4x4.Invert(proj, out Matrix4x4 invProj);

        if (!ctx.BarriersDerived) gbuffer.DepthToShaderResource();

        var (slices, steps) = QualityToCounts(pf.SSAOQuality);
        float frameIdx = ctx.DeterministicCapture ? 0f : frameCounter;
        *(GtaoConstants*)cbMapped = new GtaoConstants {
            Projection = Matrix4x4.Transpose(proj), InvProjection = Matrix4x4.Transpose(invProj),
            View = Matrix4x4.Transpose(view),
            Radius = pf.SSAORadius, Intensity = pf.SSAOIntensity, Power = pf.SSAOPower, Thickness = pf.SSAOThickness,
            TexelSize = new Vector2(1f / gtaoA.Width, 1f / gtaoA.Height),
            MultiBounce = pf.SSAOMultiBounce ? 1f : 0f, SliceCount = slices, StepCount = steps, FrameIndex = frameIdx,
        };

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
        gtaoA.ColorToShaderResource();
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
