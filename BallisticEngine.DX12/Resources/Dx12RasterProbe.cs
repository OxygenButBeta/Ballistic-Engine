using System;
using System.Numerics;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// P7.2 NO-RT DDGI probe update — per-probe G-buffer RASTER (the no-hardware-ray-tracing far-field). See
// Docs/Plans/dx12-lumen-gi-plan.md Phase 7 + RasterProbe.hlsl. The DDGI world cache (Dx12Ddgi) is decoupled
// from its ray source by the rayData buffer; P7.2 swaps the PRODUCER of rayData (rasterize+relight a small
// per-probe cube instead of inline RayQuery), reusing ~99% of the cache (blend/gather/Chebyshev/multibounce/
// round-robin/warm-up/determinism). This is the GI for GPUs without hardware ray tracing (the audience floor).
//
// ★ THIS sub-phase is P7.2a — MEASUREMENT ONLY (the user-demanded go/no-go gate). It renders ONE probe at the
// camera position, 6 cube faces of a small G-buffer (albedo + world-normal + depth), times the cost, and
// (debug) blits the albedo cube to ssgiTarget so we can SEE the rasterized probe is correct geometry. NO rayData
// write, NO blend, NO grid — because the naive full grid is 12,288 geometry passes/frame (impossible) and even
// amortized (round-robin + sleeping → ~128 probes × 6 faces) it is borderline on a GTX-1660 and may need a
// reduced-geometry proxy. We measure ONE probe's 6-face cost FIRST, then decide the grid (P7.2c) from the number.
//
// Geometry render reuses the EXISTING per-submesh draw loop via a caller-supplied delegate (the renderer owns
// the CBV ring + material SRV heap), invoked once per face with the probe-face view-projection. The PSO mirrors
// GBuffer.hlsl's vertex stage + DrawConstants CBV + 6-material SRV table (so the same loop drives it) but writes
// only albedo+normal (2 MRT) + depth — the relight (P7.2b) needs albedo, world normal, world pos (from depth).
public sealed class Dx12RasterProbe : IDisposable {
    readonly Dx12Device dev;

    public const int FaceRes = 24;                       // per-face G-buffer resolution (research: 16-32px; midpoint)
    const Format AlbedoFmt = Format.R16G16B16A16_Float;  // albedo cube (rgb albedo, a = emissive flag)
    const Format NormalFmt = Format.R16G16B16A16_Float;  // world-normal cube ([0,1]-packed)
    const Format DepthFmt = Format.D32_Float;

    // Cube G-buffer resources (6-face TextureCube each). Albedo + normal are RTV+SRV (SRV = the debug/relight
    // read); depth is DSV-only (the probe G-buffer depth is throwaway — we don't read it in P7.2a).
    ID3D12Resource albedoCube, normalCube, depthCube;
    ID3D12DescriptorHeap rtvHeap;     // 6 faces × 2 MRT = 12 RTVs (face f → [f*2+0]=albedo, [f*2+1]=normal)
    ID3D12DescriptorHeap dsvHeap;     // 6 face DSVs
    uint rtvInc, dsvInc;
    int albedoSrv = -1;               // persistent cube SRV (debug blit reads it)
    ResourceStates albedoState, normalState;   // depthCube stays DepthWrite the whole time (DSV-only, never read)

    // Per-draw G-buffer PSO (RasterProbe.hlsl) — vertex stage + DrawConstants(b0) + 6 material SRVs(t0..t5) +
    // wrap sampler, IDENTICAL to GBuffer.hlsl so the existing draw loop drives it. 2 MRT + depth.
    public ID3D12RootSignature GeoRootSig => geoRootSig;
    public ID3D12PipelineState GeoPso => geoPso;
    ID3D12RootSignature geoRootSig;
    ID3D12PipelineState geoPso;
    public const int MaterialSrvCount = 6;

    // Debug blit (RasterProbeDebug.hlsl): equirect-unwrap the cube → ssgiTarget. CBV b0 + SRV t0 cube + UAV u0.
    ID3D12RootSignature dbgRootSig;
    ID3D12PipelineState dbgPso;
    ID3D12Resource dbgCb; unsafe byte* dbgCbMapped;

    bool built;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct DebugConstants { public Vector4 Params; }   // x=screenW y=screenH z=mode w=exposureScale

    public bool Allocated => albedoCube != null;
    public long VramBytes => (long)FaceRes * FaceRes * 6 * (8 + 8 + 4);   // albedo+normal RGBA16F + depth D32

    public Dx12RasterProbe(Dx12Device device) { dev = device; }

    public unsafe void Build() {
        if (built) return;
        built = true;
        AllocCubes();
        BuildGeoPso();
        BuildDebugPso();
    }

    void AllocCubes() {
        ID3D12Resource CubeRt(Format fmt, ResourceStates init) {
            var d = ResourceDescription.Texture2D(fmt, (uint)FaceRes, (uint)FaceRes, arraySize: 6, mipLevels: 1);
            d.Flags = ResourceFlags.AllowRenderTarget;
            return dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None, d, init);
        }
        albedoCube = CubeRt(AlbedoFmt, ResourceStates.PixelShaderResource); albedoState = ResourceStates.PixelShaderResource;
        normalCube = CubeRt(NormalFmt, ResourceStates.PixelShaderResource); normalState = ResourceStates.PixelShaderResource;

        var dd = ResourceDescription.Texture2D(Format.R32_Typeless, (uint)FaceRes, (uint)FaceRes, arraySize: 6, mipLevels: 1);
        dd.Flags = ResourceFlags.AllowDepthStencil;
        var clear = new ClearValue(DepthFmt, 1.0f, 0);
        depthCube = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            dd, ResourceStates.DepthWrite, clear);

        rtvHeap = dev.Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 12));
        rtvInc = dev.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        dsvHeap = dev.Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 6));
        dsvInc = dev.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.DepthStencilView);

        for (int f = 0; f < 6; f++) {
            CreateFaceRtv(albedoCube, AlbedoFmt, f, f * 2 + 0);
            CreateFaceRtv(normalCube, NormalFmt, f, f * 2 + 1);
            dev.Device.CreateDepthStencilView(depthCube, new DepthStencilViewDescription {
                Format = DepthFmt, ViewDimension = DepthStencilViewDimension.Texture2DArray,
                Texture2DArray = new Texture2DArrayDepthStencilView { MipSlice = 0, FirstArraySlice = (uint)f, ArraySize = 1 },
            }, DsvHandle(f));
        }

        // Persistent cube SRV for the debug blit (full cube).
        albedoSrv = Dx12Backend.SrvStore.Allocate();
        dev.Device.CreateShaderResourceView(albedoCube, new ShaderResourceViewDescription {
            Format = AlbedoFmt, ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.TextureCube,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            TextureCube = new TextureCubeShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        }, Dx12Backend.SrvStore.Cpu(albedoSrv));
    }

    void CreateFaceRtv(ID3D12Resource res, Format fmt, int face, int rtvSlot) {
        dev.Device.CreateRenderTargetView(res, new RenderTargetViewDescription {
            Format = fmt, ViewDimension = RenderTargetViewDimension.Texture2DArray,
            Texture2DArray = new Texture2DArrayRenderTargetView {
                MipSlice = 0, FirstArraySlice = (uint)face, ArraySize = 1, PlaneSlice = 0 },
        }, RtvHandle(rtvSlot));
    }

    CpuDescriptorHandle RtvHandle(int slot) => new(rtvHeap.GetCPUDescriptorHandleForHeapStart(), slot, rtvInc);
    CpuDescriptorHandle DsvHandle(int face) => new(dsvHeap.GetCPUDescriptorHandleForHeapStart(), face, dsvInc);

    unsafe void BuildGeoPso() {
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var matRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, MaterialSrvCount, baseShaderRegister: 0);
        var matTable = new RootParameter1(new RootDescriptorTable1(matRange), ShaderVisibility.Pixel);
        var wrap = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 16, ComparisonFunction = ComparisonFunction.Never,
            MinLOD = 0, MaxLOD = float.MaxValue,
        };
        geoRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout,
                new[] { cbv, matTable }, new[] { wrap })));

        string hlsl = EmbeddedShaderSource.ReadHlsl("RasterProbe.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, hlsl, "VSMain", "RasterProbe.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, hlsl, "PSMain", "RasterProbe.hlsl");
        var layout = new InputLayoutDescription(
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 0, 1),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 0, 2),
            new InputElementDescription("TANGENT", 0, Format.R32G32B32A32_Float, 0, 3));
        geoPso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = geoRootSig, VertexShader = vs, PixelShader = ps, InputLayout = layout,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullClockwise,   // back-face cull, CCW-from-front (geometry parity)
            BlendState = BlendDescription.Opaque, DepthStencilState = DepthStencilDescription.Default,
            RenderTargetFormats = new[] { AlbedoFmt, NormalFmt },
            DepthStencilFormat = DepthFmt, SampleDescription = new SampleDescription(1, 0),
        });
    }

    unsafe void BuildDebugPso() {
        var cbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var srv = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1, 0);
        var uav = new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, 1, 0);
        var samp = new StaticSamplerDescription(ShaderVisibility.All, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1, ComparisonFunction = ComparisonFunction.Never,
            MinLOD = 0, MaxLOD = float.MaxValue,
        };
        dbgRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None,
                new[] { cbv, new RootParameter1(new RootDescriptorTable1(srv), ShaderVisibility.All),
                        new RootParameter1(new RootDescriptorTable1(uav), ShaderVisibility.All) },
                new[] { samp })));
        string hlsl = EmbeddedShaderSource.ReadHlsl("RasterProbeDebug.hlsl");
        dbgPso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription {
            RootSignature = dbgRootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, hlsl, "CSMain", "RasterProbeDebug.hlsl"),
        });
        int sz = (System.Runtime.InteropServices.Marshal.SizeOf<DebugConstants>() + 255) & ~255;
        dbgCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)sz), ResourceStates.GenericRead);
        dbgCbMapped = dbgCb.Map<byte>(0);
    }

    // The 6 cube-face view-projection matrices for a probe at `pos`. +X,-X,+Y,-Y,+Z,-Z, 90° FOV, near/far for
    // the probe's local visibility range (far = the DDGI maxRayDist scale; here a generous 60m). Row-vector
    // convention (mul(v, M)) matching the renderer (ToNumerics path) — DX z∈[0,1] perspective.
    public static void FaceMatrices(Vector3 pos, float near, float far, Span<Matrix4x4> outViewProj) {
        // (forward, up) per face — standard cubemap basis (LH, +Z forward to match a TextureCube lookup).
        Vector3[] fwd = { new(1,0,0), new(-1,0,0), new(0,1,0), new(0,-1,0), new(0,0,1), new(0,0,-1) };
        Vector3[] up  = { new(0,1,0), new(0,1,0), new(0,0,-1), new(0,0,1), new(0,1,0), new(0,1,0) };
        Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI * 0.5f, 1.0f, near, far);
        for (int f = 0; f < 6; f++) {
            Matrix4x4 view = Matrix4x4.CreateLookAt(pos, pos + fwd[f], up[f]);
            outViewProj[f] = view * proj;
        }
    }

    // P7.2a: render ONE probe at `probePos`, 6 faces. `drawFace(cl, viewProj)` runs the renderer's per-submesh
    // geometry loop with the geoRootSig/geoPso already bound (the renderer owns the CBV ring + material heap). The
    // viewport/scissor + face RTV+DSV are set here; the delegate only issues the draws. Returns leaving the cubes
    // in PixelShaderResource (the debug blit / future relight reads them). One command list (all 6 faces batched).
    public void RenderOneProbe(ID3D12GraphicsCommandList4 cl, Vector3 probePos,
        Action<ID3D12GraphicsCommandList4, Matrix4x4> drawFace) {
        Span<Matrix4x4> vp = stackalloc Matrix4x4[6];
        FaceMatrices(probePos, 0.05f, 60f, vp);

        To(cl, ref albedoState, albedoCube, ResourceStates.RenderTarget);
        To(cl, ref normalState, normalCube, ResourceStates.RenderTarget);
        // depthCube stays DepthWrite the whole time (created so, used only as a DSV, never read) — no transition.

        cl.RSSetViewport(0, 0, FaceRes, FaceRes);
        cl.RSSetScissorRect(FaceRes, FaceRes);
        Span<CpuDescriptorHandle> rtvs = stackalloc CpuDescriptorHandle[2];   // hoisted out of the face loop (CA2014)
        for (int f = 0; f < 6; f++) {
            CpuDescriptorHandle dsv = DsvHandle(f);
            rtvs[0] = RtvHandle(f * 2 + 0); rtvs[1] = RtvHandle(f * 2 + 1);
            cl.OMSetRenderTargets(rtvs, dsv);
            cl.ClearRenderTargetView(RtvHandle(f * 2 + 0), new Vortice.Mathematics.Color4(0, 0, 0, 0));
            cl.ClearRenderTargetView(RtvHandle(f * 2 + 1), new Vortice.Mathematics.Color4(0.5f, 0.5f, 0.5f, 1f));
            cl.ClearDepthStencilView(dsv, ClearFlags.Depth, 1.0f, 0);
            drawFace(cl, vp[f]);
        }

        To(cl, ref albedoState, albedoCube, ResourceStates.PixelShaderResource);
        To(cl, ref normalState, normalCube, ResourceStates.PixelShaderResource);
    }

    // P7.2b: render ONE probe at `probePos`, 6 faces, via the GPU-DRIVEN PROXY (Dx12GpuDrivenRenderer's lean
    // probe PSO — ~1 ExecuteIndirect/mesh-group/face instead of P7.2a's 158 per-submesh draws/face). The caller
    // MUST have already built the per-face meta (gpuDriven.ProbeBuildFaceMeta(wholeMesh, FaceMatrices(probePos))).
    // `drawFace(cl, faceIndex)` runs gpuDriven.RenderIntoProbeFace(cl, faceIndex) into the bound face RTV/DSV. This
    // method owns the cube state + per-face RTV/DSV/viewport/clear (identical to RenderOneProbe); the delegate only
    // issues the GPU-driven cull+draw. One command list (all 6 faces batched — the probe buffers are per-face
    // disjoint slices, so no upload-heap aliasing). Returns leaving the cubes in PixelShaderResource.
    public void RenderOneProbeGpuDriven(ID3D12GraphicsCommandList4 cl, Vector3 probePos,
        Action<ID3D12GraphicsCommandList4, int> drawFace) {
        To(cl, ref albedoState, albedoCube, ResourceStates.RenderTarget);
        To(cl, ref normalState, normalCube, ResourceStates.RenderTarget);
        // depthCube stays DepthWrite (DSV-only, never read) — no transition.

        cl.RSSetViewport(0, 0, FaceRes, FaceRes);
        cl.RSSetScissorRect(FaceRes, FaceRes);
        Span<CpuDescriptorHandle> rtvs = stackalloc CpuDescriptorHandle[2];
        for (int f = 0; f < 6; f++) {
            CpuDescriptorHandle dsv = DsvHandle(f);
            rtvs[0] = RtvHandle(f * 2 + 0); rtvs[1] = RtvHandle(f * 2 + 1);
            cl.OMSetRenderTargets(rtvs, dsv);
            cl.ClearRenderTargetView(RtvHandle(f * 2 + 0), new Vortice.Mathematics.Color4(0, 0, 0, 0));
            cl.ClearRenderTargetView(RtvHandle(f * 2 + 1), new Vortice.Mathematics.Color4(0.5f, 0.5f, 0.5f, 1f));
            cl.ClearDepthStencilView(dsv, ClearFlags.Depth, 1.0f, 0);
            drawFace(cl, f);
        }

        To(cl, ref albedoState, albedoCube, ResourceStates.PixelShaderResource);
        To(cl, ref normalState, normalCube, ResourceStates.PixelShaderResource);
    }

    // The probe-cube G-buffer formats — the GPU-driven proxy PSO (Dx12GpuDrivenRenderer.BuildProbePipeline) must
    // match these exactly (the OM RenderTargetFormats gate) or the indirect draw is device-removal.
    public static Format ProbeAlbedoFormat => AlbedoFmt;
    public static Format ProbeNormalFormat => NormalFmt;
    public static Format ProbeDepthFormat => DepthFmt;

    // The probe-face near/far the proxy meta must use (same as RenderOneProbe's FaceMatrices(pos, 0.05, 60)).
    public const float FaceNear = 0.05f;
    public const float FaceFar = 60f;

    // P7.2a debug: equirect-unwrap the albedo cube into `output` (ssgiTarget UAV). `outputUav`/`cubeSrv` are CPU
    // handles the caller copies into `heap` (a small shader-visible heap: slot0 = cube SRV, slot1 = output UAV).
    public unsafe void DebugBlit(ID3D12GraphicsCommandList4 cl, Dx12DescriptorHeap heap, int w, int h, float exposureScale) {
        *(DebugConstants*)dbgCbMapped = new DebugConstants { Params = new Vector4(w, h, 0f, exposureScale) };
        cl.SetDescriptorHeaps(heap.Heap);
        cl.SetComputeRootSignature(dbgRootSig);
        cl.SetPipelineState(dbgPso);
        cl.SetComputeRootConstantBufferView(0, dbgCb.GPUVirtualAddress);
        cl.SetComputeRootDescriptorTable(1, heap.Gpu(0));   // t0 cube SRV
        cl.SetComputeRootDescriptorTable(2, heap.Gpu(1));   // u0 output UAV
        cl.Dispatch((uint)((w + 7) / 8), (uint)((h + 7) / 8), 1);
    }

    public CpuDescriptorHandle AlbedoCubeSrv => Dx12Backend.SrvStore.Cpu(albedoSrv);

    static void To(ID3D12GraphicsCommandList4 cl, ref ResourceStates state, ID3D12Resource res, ResourceStates target) {
        if (state == target) return;
        cl.ResourceBarrierTransition(res, state, target);
        state = target;
    }

    public void Dispose() {
        rtvHeap?.Dispose(); dsvHeap?.Dispose();
        albedoCube?.Dispose(); normalCube?.Dispose(); depthCube?.Dispose();
        dbgCb?.Dispose(); dbgRootSig?.Dispose(); dbgPso?.Dispose();
        geoRootSig?.Dispose(); geoPso?.Dispose();
    }
}
