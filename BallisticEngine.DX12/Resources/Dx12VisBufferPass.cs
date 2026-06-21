using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Dxc;
using BallisticEngine;
using GLVector3 = System.Numerics.Vector3;

namespace BallisticEngine.DX12;

// R5 — visibility-buffer geometry path (Unreal-style deferred material / Nanite-class vis-buffer resolve).
// OPT-IN, BALLISTIC_DX12_VISBUFFER=1 + HW mesh shaders. When active it REPLACES the GPU-driven G-buffer fill:
//   1) raster a single RG32_UINT id target (VisBuffer.hlsl AS+MS+PS) — { DrawIndex+1, localMeshlet<<8|localPrim }
//      + the SAME G-buffer depth, reusing the GPU-driven meshlet substrate (per-submesh meshlet buffers).
//   2) a compute resolve (VisResolve.hlsl) reads the id per pixel, looks up the draw's VisDraw record (Mvp/Model/
//      MaterialId + BINDLESS indices of its OWN vertex + meshlet buffers), interpolates attributes, decodes the
//      material byte-identically to GBufferBindless::PSMain, and writes the fat G-buffer's 5 colors as UAVs.
// Downstream (deferred lighting / Lumen / SSR) is UNCHANGED — it reads the same fat G-buffer. Default OFF →
// the existing meshlet/ExecuteIndirect G-buffer fill runs → byte-identical.
//
// WHY a per-draw VisDraw with bindless geometry indices: this engine has PER-MESH vertex buffers + PER-SUBMESH
// meshlet buffers (not one global geometry buffer). A compute pass runs once over the screen and can't rebind
// per pixel, so each draw's buffers are addressed bindlessly (ResourceDescriptorHeap[idx]).
internal sealed class Dx12VisBufferPass : IDisposable {
    readonly Dx12Device dev;
    readonly Dx12GpuDrivenRenderer gpu;
    ID3D12RootSignature visRootSig, resolveRootSig;
    ID3D12PipelineState visPso, resolvePso;
    ID3D12Resource visTarget;            // RG32_UINT
    ID3D12DescriptorHeap visRtvHeap;
    CpuDescriptorHandle visRtv;
    int visSrvSlot = -1;                 // bindless heap slot for VisId SRV (resolve reads it via the CB index)
    ID3D12Resource resolveCb; unsafe byte* resolveCbMapped; long resolveCbStride;
    int w, h;

    [StructLayout(LayoutKind.Sequential)]
    struct ResolveConstants {
        public Matrix4x4 InvViewProj, ViewProjCur, ViewProjPrev;
        public Vector2 RtSize; public float NormalLodBias; public uint VisIdIndex;   // bindless slot of the VisId target
    }

    public bool Available => visPso != null && resolvePso != null;

    public Dx12VisBufferPass(Dx12Device device, Dx12GpuDrivenRenderer gpuDriven) {
        dev = device; gpu = gpuDriven;
        if (!dev.HasMeshShaders) return;
        BuildPipelines();
    }

    unsafe void BuildPipelines() {
        // Vis raster root sig: matches VisBuffer.hlsl — root const b0 (DrawIndex/MeshletBase/Count), root SRV
        // t0..t9 (the raster only reads PerDraws t0 + meshlet t2-t5 + Positions t6), CBV b1 + b2, point sampler s1.
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
        // UINT render targets reject the default Opaque blend on this driver (the PSO create E_INVALIDARGs). Build a
        // fully-disabled blend explicitly (no blend, no logic op, write all channels) — valid for a UINT id target.
        var visBlend = new BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
        visBlend.RenderTarget[0] = new RenderTargetBlendDescription {
            BlendEnable = false, LogicOpEnable = false,
            SourceBlend = Blend.One, DestinationBlend = Blend.Zero, BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One, DestinationBlendAlpha = Blend.Zero, BlendOperationAlpha = BlendOperation.Add,
            LogicOp = LogicOp.Noop, RenderTargetWriteMask = ColorWriteEnable.All,
        };
        visPso = Dx12MeshShaderPso.Create(dev.Device, visRootSig, asb, msb, psb,
            RasterizerDescription.CullClockwise, visBlend, DepthStencilDescription.Default,
            new[] { Format.R32G32_UInt }, Dx12GBuffer.DepthFormat);

        // Resolve compute root sig: CBV b0 + root SRV t0 (VisDraws) + t1 (GpuMaterials) + a UAV descriptor TABLE for
        // u0..u4 (texture UAVs can't be root UAVs) + s0 LinearWrap + directly-indexed (the resolve reads the VisId
        // target, geometry buffers + material textures all via ResourceDescriptorHeap[]). VisId is a Texture2D, so
        // it's read BINDLESSLY (its slot is in the CB), not as a root SRV (root SRVs are buffer-address only). Param
        // order: 0 = CBV b0, 1 = SRV t0 (VisDraws), 2 = SRV t1 (GpuMaterials), 3 = UAV table u0..u4.
        var rp = new List<RootParameter1> {
            new(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All),
            new(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All),  // t0 VisDraws
            new(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.All),  // t1 GpuMaterials
        };
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
        // The resolve binds ONE CBV_SRV_UAV heap (DX12 allows a single shader-visible CBV_SRV_UAV heap at a time):
        // the shared BindlessHeap — its ResourceDescriptorHeap[] serves the geometry/material/VisId bindless reads.
        // The UAV descriptor TABLE (u0..u4) must live in that SAME heap. Reserve 5 CONTIGUOUS persistent slots in
        // the bindless heap once; the table points at them. The G-buffer color UAVs are (re)written into them each
        // frame (the targets can change on resize/scene swap), but Reset/material-rebuild frees the slots — so
        // re-reserve lazily (gpuDriven owns the slots, see ReserveVisResolveUavs).
    }

    public void EnsureTarget(int width, int height) {
        if (visTarget != null && w == width && h == height) return;
        w = width; h = height;
        if (visTarget != null) dev.DeferredRelease(visTarget);
        var desc = ResourceDescription.Texture2D(Format.R32G32_UInt, (uint)width, (uint)height, 1, 1);
        desc.Flags = ResourceFlags.AllowRenderTarget;
        visTarget = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            desc, ResourceStates.RenderTarget, new ClearValue(Format.R32G32_UInt, new Vortice.Mathematics.Color4(0, 0, 0, 0)));
        visTarget.Name = "VisBuffer";
        visState = ResourceStates.RenderTarget;
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

    ResourceStates visState = ResourceStates.RenderTarget;

    // R5 — DRIVE THE WHOLE VIS-BUFFER GEOMETRY PHASE. Records into the OPEN frame command list (ExecuteSync on the
    // frame thread just appends). The caller passes the SAME viewProj / cull / motion data the meshlet path uses, the
    // live G-buffer (for depth DSV + the 5 color UAVs), and the renderer list. Returns the submesh draw count.
    //   raster: bind vis RTV + G-buffer DSV, clear, run the meshlet vis draw loop (gpu.RenderVis populates VisDraws).
    //   resolve: transition vis target→SRV + G-buffer colors→UAV, dispatch the resolve, transition colors→ShaderRead.
    // After this returns the G-buffer's 5 colors are in PIXEL|NON_PIXEL shader-read (what the deferred pass expects)
    // and depth is in DEPTH_WRITE (the sky pass transitions it later, same as the raster path).
    public unsafe int Render(Dx12GBuffer gbuffer, List<IStaticMeshRenderer> renderers,
        Matrix4x4 viewProj, Vector4[] frustumPlanes, Vector3 cameraPos, bool coneCull,
        Matrix4x4 viewProjUnjittered, Matrix4x4 view, float near, float far,
        Matrix4x4 viewProjCurUnjittered, Matrix4x4 viewProjPrevUnjittered, float normalLodBias) {
        if (!Available) return 0;
        EnsureTarget(gbuffer.Width, gbuffer.Height);

        int draws = 0;
        // === 1) RASTER the vis-id (mesh-shader) into visTarget + the G-buffer depth. ===
        dev.ExecuteSync(cl4 => {
            var cl = cl4.QueryInterfaceOrNull<ID3D12GraphicsCommandList6>();
            if (cl == null) return;
            // Vis target → render-target (it sits in PixelShaderResource from last frame's resolve read).
            if (visState != ResourceStates.RenderTarget) {
                cl.ResourceBarrierTransition(visTarget, visState, ResourceStates.RenderTarget);
                visState = ResourceStates.RenderTarget;
            }
            gbuffer.DepthTransitionPublic(cl, ResourceStates.DepthWrite);
            cl.RSSetViewport(0, 0, w, h);
            cl.RSSetScissorRect(w, h);
            // Clear the vis-id target (miss = 0). Do NOT clear depth — the geometry pass already cleared the SHARED
            // G-buffer depth to 1.0 and any CPU-path (skinned/custom) geometry wrote into it before us; the vis
            // raster depth-tests/writes against that shared depth so it composites correctly with CPU geometry.
            cl.ClearRenderTargetView(visRtv, new Vortice.Mathematics.Color4(0, 0, 0, 0));
            Span<CpuDescriptorHandle> rtvs = stackalloc CpuDescriptorHandle[1] { visRtv };
            cl.OMSetRenderTargets(rtvs, gbuffer.DsvHandle);
            int cpuDrawIndex = 0;
            draws = gpu.RenderVis(cl, renderers, visRootSig, visPso, viewProj, frustumPlanes, cameraPos, coneCull,
                viewProjUnjittered, view, near, far, ref cpuDrawIndex);
            cl.Dispose();
        });
        if (draws == 0) return 0;

        // === 2) RESOLVE compute → write the fat G-buffer colors as UAVs. ===
        // Resolve CB.
        long cbOff = (long)dev.FrameSlot * resolveCbStride;
        Matrix4x4.Invert(viewProjUnjittered, out Matrix4x4 invVp);
        *(ResolveConstants*)(resolveCbMapped + cbOff) = new ResolveConstants {
            InvViewProj = Matrix4x4.Transpose(invVp),
            ViewProjCur = Matrix4x4.Transpose(viewProjCurUnjittered),
            ViewProjPrev = Matrix4x4.Transpose(viewProjPrevUnjittered),
            RtSize = new Vector2(w, h), NormalLodBias = normalLodBias, VisIdIndex = (uint)visSrvSlot,
        };
        // Reserve 5 contiguous UAV slots IN THE BINDLESS HEAP (DX12 binds only one CBV_SRV_UAV heap; the resolve's
        // ResourceDescriptorHeap[] reads + the UAV table must share it). (Re)write the live G-buffer color UAVs into
        // them each frame (the targets change on resize/scene swap).
        int uavBase = gpu.ReserveVisResolveUavs();
        for (int i = 0; i < Dx12GBuffer.RtCount; i++)
            gbuffer.CreateColorUav(i, Dx12Backend.BindlessHeap.Cpu(uavBase + i));

        dev.ExecuteSync(cl => {
            // Vis target → shader resource for the resolve read.
            if (visState != ResourceStates.NonPixelShaderResource) {
                cl.ResourceBarrierTransition(visTarget, visState, ResourceStates.NonPixelShaderResource);
                visState = ResourceStates.NonPixelShaderResource;
            }
            gbuffer.ColorsToUav(cl);   // 5 colors → UnorderedAccess (resolve write)

            // GOTCHA (documented): SetDescriptorHeaps MUST precede SetComputeRootSignature for a directly-indexed
            // root sig, or the SM6.6 ResourceDescriptorHeap[] bindless reads in the resolve full-GPU-hang.
            cl.SetDescriptorHeaps(Dx12Backend.BindlessHeap.Heap);
            cl.SetComputeRootSignature(resolveRootSig);
            cl.SetPipelineState(resolvePso);
            cl.SetComputeRootConstantBufferView(0, resolveCb.GPUVirtualAddress + (ulong)cbOff);
            cl.SetComputeRootShaderResourceView(1, gpu.VisDrawsAddress);            // t0 VisDraws
            cl.SetComputeRootShaderResourceView(2, gpu.MaterialsGpuAddress);        // t1 GpuMaterials
            cl.SetComputeRootDescriptorTable(3, Dx12Backend.BindlessHeap.Gpu(uavBase));  // u0..u4 (VisId read bindlessly via the CB index)
            int gx = (w + 7) / 8, gy = (h + 7) / 8;
            cl.Dispatch((uint)gx, (uint)gy, 1);

            gbuffer.ColorsToShaderRead(cl);   // 5 colors → PIXEL|NON_PIXEL (deferred lighting reads them)
        });
        return draws;
    }

    public void Dispose() {
        visRootSig?.Dispose(); resolveRootSig?.Dispose(); visPso?.Dispose(); resolvePso?.Dispose();
        visTarget?.Dispose(); visRtvHeap?.Dispose(); resolveCb?.Dispose();
    }
}
