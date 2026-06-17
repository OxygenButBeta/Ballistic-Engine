using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;        // PrimitiveTopology
using Vortice.Direct3D12;
using Vortice.Dxc;             // DxcShaderStage
using Vortice.DXGI;            // Format, SampleDescription
using BallisticEngine;         // SceneColorTintFeature, RenderFeature

namespace BallisticEngine.DX12;

// PHASE-3 (chunk 20) — the GPU side of the proof feature's full-screen blit. Owns the rootsig/PSO/CB/heap +
// a scratch HDR copy of SceneColor (a DX12 RT cannot be sampled AND rendered to at once, so a tint that
// ReadWrites SceneColor in place must ping-pong through a scratch). DX12HDRenderer owns ONE of these and
// resolution-manages the scratch; the Dx12FeaturePassRecorder delegates BlitFullscreen here.
//
// DELIBERATELY MINIMAL (design §3 / D4): the verb surface a feature sees is IFeaturePassRecorder; this is the
// backend that satisfies the ONE blit the chunk-20 proof needs. The shader name "SceneColorTint" selects the
// tint PSO; the param values (Tint/Strength) are pulled from the feature instance the recorder carries. A real
// feature set grows this (more PSOs / a material-name → asset lookup) on demand, logged in the design doc.
//
// Pixel-neutral when off: the proof feature defaults Strength=1 (visible), but with the feature ABSENT this
// class is never invoked (the bridge adds no adapter) → byte-identical to golden. With Strength=0 the shader
// is a passthrough (lerp t=0), so a present-but-off feature is also pixel-neutral.
public sealed class Dx12FeatureBlitter : IDisposable {
    [StructLayout(LayoutKind.Sequential)]
    struct TintConstants { public Vector3 Tint; public float Strength; }

    readonly Dx12Device dev;
    ID3D12RootSignature rootSig;     // TintConstants CBV (b0) + 1 SRV (t0: scene-color scratch) + sampler
    ID3D12PipelineState tintPso;
    ID3D12Resource cb;
    unsafe byte* cbMapped;
    Dx12DescriptorHeap srvVisible;   // [0] = the scratch SRV, copied per blit
    Dx12OffscreenTarget scratch;     // HDR copy of SceneColor (full render res)

    public unsafe Dx12FeatureBlitter(Dx12Device device, int width, int height) {
        dev = device;
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srvRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, baseShaderRegister: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var samp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        rootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, new[] { cbv, srvTable }, new[] { samp })));

        string hlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("SceneColorTint.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "SceneColorTint.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "SceneColorTint.hlsl");
        tintPso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = rootSig, VertexShader = vs, PixelShader = ps, InputLayout = null,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.None,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.HdrFormat },
            DepthStencilFormat = Format.Unknown, SampleDescription = new SampleDescription(1, 0),
        });

        int cbSize = (Marshal.SizeOf<TintConstants>() + 255) & ~255;
        cb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)cbSize), ResourceStates.GenericRead);
        cbMapped = cb.Map<byte>(0);
        srvVisible = new Dx12DescriptorHeap(dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, shaderVisible: true, framesInFlight: dev.FramesInFlight);
        AllocScratch(width, height);
    }

    public void Resize(int width, int height) => AllocScratch(width, height);

    void AllocScratch(int width, int height) {
        scratch?.Dispose();
        scratch = new Dx12OffscreenTarget(dev, Math.Max(1, width), Math.Max(1, height),
            withDepth: false, colorFormat: Dx12OffscreenTarget.HdrFormat, colorReadable: true);
    }

    // Tint `dst` in place (src == dst == the live SceneColor): copy SceneColor → scratch, then a full-screen
    // tint pass samples the scratch and writes back into SceneColor. `feature` carries the Tint/Strength params.
    public unsafe void Tint(Dx12OffscreenTarget sceneColor, RenderFeature feature) {
        Vector3 tint = Vector3.One; float strength = 1f;
        if (feature is SceneColorTintFeature t) { tint = t.Tint; strength = t.Strength; }

        // Size the scratch to the live SceneColor (FSR output differs from the native target). Recreate only on a
        // genuine size change (GLFrameBuffer.Resize gotcha parity — recreating every frame flickers/wastes).
        if (scratch.Width != sceneColor.Width || scratch.Height != sceneColor.Height)
            AllocScratch(sceneColor.Width, sceneColor.Height);

        *(TintConstants*)cbMapped = new TintConstants { Tint = tint, Strength = strength };

        // SceneColor → scratch (the readable copy). CopyColorFrom handles both targets' transitions and leaves
        // both in RenderTarget. Then bring scratch to SRV so the tint pass can sample it.
        scratch.CopyColorFrom(sceneColor);
        scratch.ColorToShaderResource();

        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        dev.Device.CopyDescriptorsSimple(1, srvVisible.Cpu(0), scratch.ColorSrvCpu, heapType);
        sceneColor.RenderColorOnly(cl => {
            cl.SetGraphicsRootSignature(rootSig);
            cl.SetPipelineState(tintPso);
            cl.SetDescriptorHeaps(srvVisible.Heap);
            cl.SetGraphicsRootConstantBufferView(0, cb.GPUVirtualAddress);
            cl.SetGraphicsRootDescriptorTable(1, srvVisible.Gpu(0));
            cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            cl.DrawInstanced(3, 1, 0, 0);
        });
    }

    public void Dispose() {
        scratch?.Dispose();
        srvVisible?.Dispose();
        cb?.Dispose();
        tintPso?.Dispose();
        rootSig?.Dispose();
    }
}
