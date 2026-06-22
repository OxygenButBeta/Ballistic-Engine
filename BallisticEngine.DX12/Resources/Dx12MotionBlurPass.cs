using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

public sealed class Dx12MotionBlurPass : IRenderPass, IDisposable {
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.PostProcess;
    public string Name => "MotionBlur";

    public bool Enabled(Dx12FrameContext ctx) => ctx.PostFX.MotionBlurEnabled && !ctx.DeterministicCapture;

    public void Declare(Dx12PassBuilder b) {
        b.Read(b.Resource("GBuffer"));
        b.ReadWrite(b.Resource("SceneColor"));
        b.DeriveBarriers();
        b.Use(Dx12ResourceUsage.SceneColorShaderRead);
        b.Use(Dx12ResourceUsage.GBufferDepthShaderRead);
    }

    const int TileSize = 20;

    [StructLayout(LayoutKind.Sequential)]
    struct MotionBlurConstants {
        public Vector2 TexelSize;
        public Vector2 TileTexelSize;
        public float Intensity; public float MaxVelocity; public float SampleCount; public float TileSizePx;
        public float Dither; public Vector3 Pad;
    }

    readonly Dx12Device dev;
    ID3D12RootSignature rootSig;
    ID3D12PipelineState tileMaxPso, neighbourMaxPso, reconstructPso;
    Dx12FrameCb<MotionBlurConstants> cb;
    Dx12OffscreenTarget velTileA, velTileB;
    Dx12OffscreenTarget scratch;
    Dx12DescriptorHeap srvVisible;
    int renderW, renderH, tileW, tileH;

    public unsafe Dx12MotionBlurPass(Dx12Device device, int width, int height) {
        dev = device;
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 4, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var samp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        rootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { samp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("MotionBlur.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "MotionBlur.hlsl");
        ID3D12PipelineState MakePso(string entry, Format rtFormat) => dev.Device.CreateGraphicsPipelineState(
            new GraphicsPipelineStateDescription {
                RootSignature = rootSig, VertexShader = vs,
                PixelShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, entry, "MotionBlur.hlsl"),
                InputLayout = null, PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
                RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
                DepthStencilState = DepthStencilDescription.None,
                RenderTargetFormats = new[] { rtFormat }, DepthStencilFormat = Format.Unknown,
                SampleDescription = new SampleDescription(1, 0),
            });
        tileMaxPso      = MakePso("PSTileMax",      Format.R16G16_Float);
        neighbourMaxPso = MakePso("PSNeighbourMax", Format.R16G16_Float);
        reconstructPso  = MakePso("PSReconstruct",  Dx12OffscreenTarget.HdrFormat);

        cb = new Dx12FrameCb<MotionBlurConstants>(dev);
        srvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 12, shaderVisible: true, framesInFlight: dev.FramesInFlight);
        renderW = width; renderH = height;
        AllocTargets(width, height);
    }

    public void Resize(int width, int height) { renderW = width; renderH = height; AllocTargets(width, height); }

    void AllocTargets(int width, int height) {
        if (velTileA is { IsPlaced: false }) velTileA.Dispose();
        if (velTileB is { IsPlaced: false }) velTileB.Dispose();
        scratch?.Dispose();
        tileW = Math.Max(1, (width  + TileSize - 1) / TileSize);
        tileH = Math.Max(1, (height + TileSize - 1) / TileSize);
        velTileA = Dx12RenderTargetPool.AllocOrPool(dev, "mbVelTileA", tileW, tileH, Format.R16G16_Float, colorReadable: true, allowUav: false);
        velTileB = Dx12RenderTargetPool.AllocOrPool(dev, "mbVelTileB", tileW, tileH, Format.R16G16_Float, colorReadable: true, allowUav: false);
        scratch  = new Dx12OffscreenTarget(dev, width, height, withDepth: false, colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
    }

    public unsafe void Record(Dx12FrameContext ctx) {
        var pf = ctx.PostFX;
        Dx12RenderTargetPool.PoolBarrier(ctx.Dev, "mbVelTileA", "mbVelTileB");
        Dx12GBuffer gbuffer = ctx.GBuffer;
        Dx12OffscreenTarget scene = ctx.SceneColor;
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;

        if (!ctx.BarriersDerived) { scene.ColorToShaderResource(); gbuffer.DepthToShaderResource(); }

        cb.Write(new MotionBlurConstants {
            TexelSize = new Vector2(1f / renderW, 1f / renderH),
            TileTexelSize = new Vector2(1f / tileW, 1f / tileH),
            Intensity = pf.MotionBlurIntensity, MaxVelocity = pf.MotionBlurMaxVelocity,
            SampleCount = Math.Max(1, pf.MotionBlurSamples), TileSizePx = TileSize,
            Dither = 1f,
        });

        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(0), scene.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(1), gbuffer.ColorSrvCpu(Dx12GBuffer.MotionRtIndex), heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(2), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(3), gbuffer.ColorSrvCpu(Dx12GBuffer.MotionRtIndex), heapType);
        velTileA.RenderColorOnly(cl => DrawPass(cl, tileMaxPso, 0));

        velTileA.ColorToShaderResource();
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(4), scene.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(5), gbuffer.ColorSrvCpu(Dx12GBuffer.MotionRtIndex), heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(6), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(7), velTileA.ColorSrvCpu, heapType);
        velTileB.RenderColorOnly(cl => DrawPass(cl, neighbourMaxPso, 4));

        velTileB.ColorToShaderResource();
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(8),  scene.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(9),  gbuffer.ColorSrvCpu(Dx12GBuffer.MotionRtIndex), heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(10), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(11), velTileB.ColorSrvCpu, heapType);
        scratch.RenderColorOnly(cl => DrawPass(cl, reconstructPso, 8));

        scratch.ColorToShaderResource();
        scene.CopyColorFrom(scratch);
    }

    void DrawPass(ID3D12GraphicsCommandList4 cl, ID3D12PipelineState pso, int srvSlot) {
        cl.SetGraphicsRootSignature(rootSig); cl.SetPipelineState(pso);
        cl.SetDescriptorHeaps(srvVisible.Heap);
        cl.SetGraphicsRootConstantBufferView(0, cb.Gpu);
        cl.SetGraphicsRootDescriptorTable(1, srvVisible.Gpu(srvSlot));
        cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        cl.DrawInstanced(3, 1, 0, 0);
    }

    public void Dispose() {
        velTileA?.Dispose(); velTileB?.Dispose(); scratch?.Dispose();
        srvVisible?.Dispose(); cb?.Dispose();
        tileMaxPso?.Dispose(); neighbourMaxPso?.Dispose(); reconstructPso?.Dispose();
        rootSig?.Dispose();
    }
}
