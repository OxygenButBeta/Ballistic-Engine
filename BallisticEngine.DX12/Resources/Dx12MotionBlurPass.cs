using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;        // PrimitiveTopology
using Vortice.Direct3D12;
using Vortice.Dxc;             // DxcShaderStage
using Vortice.DXGI;            // Format, SampleDescription

namespace BallisticEngine.DX12;

// McGuire-style per-pixel velocity MOTION BLUR for the DX12 deferred renderer ("A Reconstruction Filter for
// Plausible Motion Blur", McGuire et al. 2012). Tile-max → neighbour-max → jittered reconstruction gather, all
// from the G-buffer motion vector (RT4, prevUV-currUV — the SAME source TaaPass reprojects with).
//
// REGISTRATION ORDER (this pass is NOT auto-registered — the renderer wires it). Event = PostProcess (650), the
// SAME bucket as TAA/FSR/Composite; the stable tiebreak is registration order, so register it:
//     ... TaaPass (650) / FsrPass (650) ...  ← the upscaler/AA resolve produces the final-resolution HDR color
//     Dx12MotionBlurPass (650)               ← THIS pass: smears the RESOLVED scene color
//     [Dx12DepthOfFieldPass (650), if any]   ← DoF blurs AFTER the motion smear
//     Dx12CompositePass (700)                ← tonemap reads the (smeared) scene color
// i.e. register motion blur AFTER FSR/TAA and BEFORE DoF (and before Composite, which is a later event anyway).
//
// The reconstruction gather READS the scene color while it must WRITE it (a fullscreen RMW that can't read+write
// the same RTV), so it renders into a pooled HDR scratch and CopyColorFrom's it back into ctx.SceneColor — the
// TaaPass resolve→copy-back pattern. Velocity = -motion (currUV-prevUV is the on-screen pixel travel direction),
// scaled by MotionBlurIntensity and clamped to MotionBlurMaxVelocity.
public sealed class Dx12MotionBlurPass : IRenderPass, IDisposable {
    // PostProcess (650): runs after the TAA/FSR resolve makes the final-resolution HDR color, before Composite.
    public Dx12RenderPassEvent Event => Dx12RenderPassEvent.PostProcess;
    public string Name => "MotionBlur";

    // Gated by the MotionBlur volume's enable. ALSO skip under deterministic capture: motion is frozen there
    // (zero velocity → a no-op smear), so skipping keeps paused/bal-render frames byte-identical.
    public bool Enabled(Dx12FrameContext ctx) => ctx.PostFX.MotionBlurEnabled && !ctx.DeterministicCapture;

    // Reads SceneColor (gather taps) + the G-buffer (motion + depth), read-modify-writes SceneColor (the smeared
    // result is copied back). Velocity tiles are pass-PRIVATE pooled scratch (not pass-boundary heads → inline).
    public void Declare(Dx12PassBuilder b) {
        b.Read(b.Resource("GBuffer"));
        b.ReadWrite(b.Resource("SceneColor"));
        b.DeriveBarriers();
        b.Use(Dx12ResourceUsage.SceneColorShaderRead);
        b.Use(Dx12ResourceUsage.GBufferDepthShaderRead);
    }

    // Tile size in px = the max blur radius (a velocity longer than this is clamped by MaxVelocity, so a tile of
    // this size captures the full smear). Matches the McGuire reference tile = max-radius.
    const int TileSize = 20;

    [StructLayout(LayoutKind.Sequential)]
    struct MotionBlurConstants {
        public Vector2 TexelSize;       // 1 / full-res
        public Vector2 TileTexelSize;   // 1 / tile-grid size
        public float Intensity; public float MaxVelocity; public float SampleCount; public float TileSizePx;
        public float Dither; public Vector3 Pad;
    }

    readonly Dx12Device dev;
    ID3D12RootSignature rootSig;        // MotionBlurConstants CBV(b0) + 4-SRV table(scene/motion/depth/tile) + sampler
    ID3D12PipelineState tileMaxPso, neighbourMaxPso, reconstructPso;
    Dx12FrameCb<MotionBlurConstants> cb; // N-buffered (FrameSlot-offset), GTAO/TAA pattern
    Dx12OffscreenTarget velTileA, velTileB;   // RG16F velocity tiles: TileMax→velTileA, NeighbourMax→velTileB
    Dx12OffscreenTarget scratch;              // HDR reconstruction output (copied back into ctx.SceneColor)
    Dx12DescriptorHeap srvVisible;            // 4 SRVs × 3 sub-passes = 12 contiguous slots
    int renderW, renderH, tileW, tileH;

    public unsafe Dx12MotionBlurPass(Dx12Device device, int width, int height) {
        dev = device;
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        // 4-SRV table: scene(t0) + motion(t1) + depth(t2) + velocity tiles(t3); each sub-pass binds the 4-slot run.
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
        // 4 SRVs per sub-pass × 3 sub-passes (TileMax / NeighbourMax / Reconstruct) = 12 fixed slots.
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
        // RG16F velocity tiles (small, transient) — pooled. The HDR reconstruction scratch is full-res.
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

        // MotionBlur is a PostProcess consumer of the resolved scene color + the G-buffer depth/motion. Emit the
        // manual head transitions only when derived barriers are off (the graph emits them otherwise). Motion RT
        // is already PixelShaderResource (the gbuffer's upstream ToShaderResource transitioned all colors).
        if (!ctx.BarriersDerived) { scene.ColorToShaderResource(); gbuffer.DepthToShaderResource(); }

        cb.Write(new MotionBlurConstants {
            TexelSize = new Vector2(1f / renderW, 1f / renderH),
            TileTexelSize = new Vector2(1f / tileW, 1f / tileH),
            Intensity = pf.MotionBlurIntensity, MaxVelocity = pf.MotionBlurMaxVelocity,
            SampleCount = Math.Max(1, pf.MotionBlurSamples), TileSizePx = TileSize,
            Dither = 1f,   // deterministic capture skips the whole pass (Enabled=false) → dither stays animated here
        });

        // --- Pass 1: TileMax (motion → velTileA). Bind the 4-slot run; only motion(t1) is read, rest are spare. ---
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(0), scene.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(1), gbuffer.ColorSrvCpu(Dx12GBuffer.MotionRtIndex), heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(2), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(3), gbuffer.ColorSrvCpu(Dx12GBuffer.MotionRtIndex), heapType);
        velTileA.RenderColorOnly(cl => DrawPass(cl, tileMaxPso, 0));

        // --- Pass 2: NeighbourMax (velTileA → velTileB). VelTile(t3) = velTileA. ---
        velTileA.ColorToShaderResource();
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(4), scene.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(5), gbuffer.ColorSrvCpu(Dx12GBuffer.MotionRtIndex), heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(6), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(7), velTileA.ColorSrvCpu, heapType);
        velTileB.RenderColorOnly(cl => DrawPass(cl, neighbourMaxPso, 4));

        // --- Pass 3: Reconstruction gather (scene + motion + depth + neighbour-max → scratch). ---
        velTileB.ColorToShaderResource();
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(8),  scene.ColorSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(9),  gbuffer.ColorSrvCpu(Dx12GBuffer.MotionRtIndex), heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(10), gbuffer.DepthSrvCpu, heapType);
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(11), velTileB.ColorSrvCpu, heapType);
        scratch.RenderColorOnly(cl => DrawPass(cl, reconstructPso, 8));

        // Copy the smeared HDR result back into the canonical scene color (TaaPass resolve→copy-back pattern).
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
