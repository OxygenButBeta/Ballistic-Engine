using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Dxc;
using BallisticEngine;

namespace BallisticEngine.DX12;

// R5 — visibility-buffer pass. Raster a single RG32_UINT id target with the mesh-shader cull pipeline (VisBuffer.
// hlsl), then a compute pass (VisResolve.hlsl) resolves each pixel's material into the fat G-buffer's color UAVs.
// Reuses the GPU-driven renderer's PerDraws / GpuMaterials / meshlet buffers. Opt-in; null/disabled unless HW mesh
// shaders are present and the renderer routes whole-mesh geometry here.
internal sealed class Dx12VisBufferPass : IDisposable {
    readonly Dx12Device dev;
    readonly Dx12GpuDrivenRenderer gpu;
    ID3D12RootSignature visRootSig, resolveRootSig;
    ID3D12PipelineState visPso, resolvePso;
    ID3D12Resource visTarget;            // RG32_UINT
    ID3D12DescriptorHeap visRtvHeap;
    CpuDescriptorHandle visRtv;
    int visSrvSlot = -1;                 // bindless heap slot for VisId SRV (resolve t10)
    Dx12DescriptorHeap resolveHeap;      // 5 G-buffer UAVs (u0..u4) — created per frame at the live targets
    ID3D12Resource resolveCb; unsafe byte* resolveCbMapped; long resolveCbStride;
    int w, h;

    [StructLayout(LayoutKind.Sequential)]
    struct ResolveConstants {
        public Matrix4x4 InvViewProj, ViewProjCur, ViewProjPrev;
        public Vector2 RtSize; public float NormalLodBias; public float Pad;
    }

    public bool Available => visPso != null;

    public Dx12VisBufferPass(Dx12Device device, Dx12GpuDrivenRenderer gpuDriven) {
        dev = device; gpu = gpuDriven;
        if (!dev.HasMeshShaders) return;
        BuildPipelines();
    }

    unsafe void BuildPipelines() {
        // Vis raster root sig: matches MeshletGBuffer's t0/t2-t6 subset + CBV b0/b2 + s1 point. The vis PS only
        // writes the id, so it needs PerDraws(t0), Meshlets(t2), Bounds(t3), Verts(t4), Prims(t5), Pos(t6).
        var vp = new List<RootParameter1> { new(new RootConstants(0, 0, 4), ShaderVisibility.All) };
        for (int t = 0; t <= 9; t++) vp.Add(new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1((uint)t, 0), ShaderVisibility.All));
        vp.Add(new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.All));
        vp.Add(new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(2, 0), ShaderVisibility.All));
        var pointS = new StaticSamplerDescription(ShaderVisibility.All, 1, 0) {
            Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1, ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        visRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed, vp.ToArray(), new[] { pointS })));
        string vb = EmbeddedShaderSource.ReadHlsl("VisBuffer.hlsl");
        byte[] asb = Dx12ShaderCompiler.Compile(DxcShaderStage.Amplification, vb, "ASMain", "VisBuffer.hlsl");
        byte[] msb = Dx12ShaderCompiler.Compile(DxcShaderStage.Mesh, vb, "MSMain", "VisBuffer.hlsl");
        byte[] psb = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, vb, "PSMain", "VisBuffer.hlsl");
        visPso = Dx12MeshShaderPso.Create(dev.Device, visRootSig, asb, msb, psb,
            RasterizerDescription.CullClockwise, BlendDescription.Opaque, DepthStencilDescription.Default,
            new[] { Format.R32G32_UInt }, Dx12GBuffer.DepthFormat);

        // Resolve compute root sig: CBV b0 + root SRV t0..t10 + a UAV descriptor TABLE for u0..u4 + s0 LinearWrap +
        // directly-indexed. TEXTURE UAVs CANNOT be root UAVs (root UAV = a buffer GPU address only); RWTexture2D
        // must be bound through a descriptor table. Param order: 0=CBV, 1..11=root SRV t0..t10, 12=UAV table u0..u4.
        var rp = new List<RootParameter1> { new(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All) };
        for (int t = 0; t <= 10; t++) rp.Add(new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1((uint)t, 0), ShaderVisibility.All));
        var uavRange = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 5, baseShaderRegister: 0, registerSpace: 0, offsetInDescriptorsFromTableStart: 0);
        rp.Add(new RootParameter1(new RootDescriptorTable1(uavRange), ShaderVisibility.All));
        var wrapS = new StaticSamplerDescription(ShaderVisibility.All, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 16, ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        resolveRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed, rp.ToArray(), new[] { wrapS })));
        string rv = EmbeddedShaderSource.ReadHlsl("VisResolve.hlsl");
        resolvePso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription {
            RootSignature = resolveRootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, rv, "CSMain", "VisResolve.hlsl"),
        });

        resolveCbStride = 256;
        resolveCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(resolveCbStride * dev.FramesInFlight)), ResourceStates.GenericRead);
        resolveCbMapped = resolveCb.Map<byte>(0);
        // UAV staging heap (shader-visible) holding the 5 G-buffer UAVs each frame (resolve binds via a table —
        // BUT the resolve uses ROOT UAVs, not a table; root UAVs need a buffer GPU address, and textures can't be
        // root UAVs). So the resolve binds the UAVs through a DESCRIPTOR TABLE. Rework: the resolve root sig must
        // use a UAV descriptor TABLE (u0..u4), not root UAVs. (Texture UAVs are heap descriptors only.)
        resolveHeap = new Dx12DescriptorHeap(dev, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 5, shaderVisible: true, framesInFlight: dev.FramesInFlight);
    }

    public void EnsureTarget(int width, int height) {
        if (visTarget != null && w == width && h == height) return;
        w = width; h = height;
        visTarget?.Dispose();
        var desc = ResourceDescription.Texture2D(Format.R32G32_UInt, (uint)width, (uint)height, 1, 1);
        desc.Flags = ResourceFlags.AllowRenderTarget;
        visTarget = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            desc, ResourceStates.RenderTarget, new ClearValue(Format.R32G32_UInt, new Vortice.Mathematics.Color4(0, 0, 0, 0)));
        visTarget.Name = "VisBuffer";
        visRtvHeap ??= dev.Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 1));
        visRtv = visRtvHeap.GetCPUDescriptorHandleForHeapStart();
        dev.Device.CreateRenderTargetView(visTarget, null, visRtv);
        if (visSrvSlot < 0) visSrvSlot = Dx12Backend.BindlessHeap.Allocate();
        dev.Device.CreateShaderResourceView(visTarget, new ShaderResourceViewDescription {
            Format = Format.R32G32_UInt, ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
        }, Dx12Backend.BindlessHeap.Cpu(visSrvSlot));
    }

    public void Dispose() {
        visRootSig?.Dispose(); resolveRootSig?.Dispose(); visPso?.Dispose(); resolvePso?.Dispose();
        visTarget?.Dispose(); visRtvHeap?.Dispose(); resolveCb?.Dispose(); resolveHeap?.Dispose();
    }
}
