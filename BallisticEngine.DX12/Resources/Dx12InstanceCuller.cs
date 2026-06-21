using System;
using System.Numerics;
using Vortice.Direct3D12;
using Vortice.Dxc;
using GLVector3 = System.Numerics.Vector3;

namespace BallisticEngine.DX12;

// GPU-driven PER-INSTANCE frustum + Hi-Z cull (the Unreal ISM/HISM instance-cull equivalent) for the DX12
// renderer. The default instanced path uploads ALL instance matrices and draws them with a fixed CPU
// InstanceCount (no per-instance culling). This pass:
//   1) packs each instance's world matrix + the mesh's LOCAL AABB into an InstanceData SRV,
//   2) runs InstanceCull.hlsl: one thread per instance transforms the local AABB by the instance matrix (the
//      SAME 8-corner loop the CPU WorldAabb uses, bit-identical), frustum-tests + Hi-Z occlusion-tests it, and
//      atomically APPENDS the survivor's index into a compacted buffer (the atomic counter = the indirect
//      draw's InstanceCount),
//   3) draws ONE DrawIndexedInstancedIndirect with InstanceCount = the GPU counter; InstanceGBuffer.hlsl's VS
//      reads VisibleIndices[SV_InstanceID] -> Instances[idx].Model to place each surviving instance.
//
// Gated by BALLISTIC_DX12_INSTANCE_CULL. When OFF the renderer's existing upload-all + draw-all instanced path
// runs unchanged (byte-identical). When ON, only on-screen/unoccluded instances draw — and because the cull
// never false-culls (conservative Hi-Z, positive-vertex frustum test identical to the CPU path), the image
// matches the upload-all result for any scene whose instances are all visible.
//
// Reuses the GPU-driven renderer's BINDLESS material table (passed in as a GPU address + a resolved material
// id) and its Hi-Z pyramid bindless slot, so shading is byte-identical to GBufferBindless.hlsl. Self-contained
// otherwise (own PSOs/buffers) so it can't perturb the byte-critical whole-mesh RenderInto path.
public sealed class Dx12InstanceCuller : IDisposable {
    const int MaxInstances = 65536;   // per RenderInstanced call (grown by EnsureCapacity if a call exceeds it)

    readonly Dx12Device dev;

    // Cull (compute): InstCullParams CBV(b0) + Instances SRV(t0) + VisibleIndices/DrawArgs UAV(u0/u1) + Hi-Z sampler.
    ID3D12RootSignature cullRootSig;
    ID3D12PipelineState cullPso;
    // Draw: InstDrawCB root-const(b0) + Motion CBV(b1) + InstView CBV(b2) + Instances/VisibleIndices/Materials SRV(t0/t1/t2) + bindless.
    ID3D12RootSignature drawRootSig;
    ID3D12PipelineState drawPso;
    ID3D12CommandSignature cmdSig;

    // Per-frame GPU buffers (N-buffered for P0b overlap via FrameSlot stride).
    int capacity;
    ID3D12Resource instances;     unsafe byte* instancesMapped;   // InstanceData[] (UPLOAD, CPU-filled each call)
    ID3D12Resource visibleIndices;                                 // DEFAULT UAV — compacted survivor indices (cull writes)
    ID3D12Resource drawArgs;                                       // DEFAULT UAV/IndirectArgument — the cull bumps InstanceCount
    ID3D12Resource drawArgsSeed; unsafe byte* drawArgsSeedMapped;  // UPLOAD: CPU-seeded args (InstanceCount=0), copied into drawArgs
    ResourceStates drawArgsState = ResourceStates.IndirectArgument;
    ID3D12Resource cullParamCb;   unsafe byte* cullParamMapped;    // InstCullParams[ ] (256B slots), one per call
    ID3D12Resource viewCb;        unsafe byte* viewCbMapped;       // InstViewCB (b2)
    long instancesFrameStride, drawArgsFrameStride, cullParamFrameStride, viewCbFrameStride;
    int instanceStride, cullParamSlotSize, viewCbSlotSize;
    int callsThisFrame;           // distinct RenderInstanced calls this frame (each needs its own DrawArgs/param slot)
    const int MaxCallsPerFrame = 256;

    public long LastVisibleUpperBound;   // pre-cull instance count fed this frame (stats)

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct InstanceData { public Matrix4x4 Model; public Vector4 AabbMin; public Vector4 AabbMax; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct InstCullParams {
        public Vector4 P0, P1, P2, P3, P4, P5;
        public uint InstanceCount, HizEnabled, HizIndex, Pad0;
        public Matrix4x4 ViewProj, View;
        public Vector4 HizParams, HizFar;
    }
    // [IndexCountPerInstance, InstanceCount, StartIndexLocation, BaseVertexLocation, StartInstanceLocation]
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct DrawArgs { public uint IndexCount, InstanceCount, StartIndex, BaseVertex, StartInstance; }

    public Dx12InstanceCuller(Dx12Device device) {
        dev = device;
        instanceStride = System.Runtime.InteropServices.Marshal.SizeOf<InstanceData>();
        cullParamSlotSize = (System.Runtime.InteropServices.Marshal.SizeOf<InstCullParams>() + 255) & ~255;
        viewCbSlotSize = 256;   // InstViewCB (two mats = 128B, padded to a CB slot)
        BuildPipelines();
        EnsureCapacity(4096);   // a reasonable starting capacity; grows on demand
        AllocateFixedBuffers();
    }

    unsafe void BuildPipelines() {
        // --- Cull root sig: CBV b0 + SRV t0 (Instances) + UAV u0 (VisibleIndices) + UAV u1 (DrawArgs) +
        //     a static point sampler (s0) + the directly-indexed flag so the cull can sample the Hi-Z pyramid. ---
        var cullParams = new[] {
            new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All),
            new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All),
            new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(0, 0), ShaderVisibility.All),
            new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(1, 0), ShaderVisibility.All),
        };
        var pointClamp = new StaticSamplerDescription(ShaderVisibility.All, 0, 0) {
            Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        cullRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(
                RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
                cullParams, new[] { pointClamp })));
        cullPso = dev.CreateComputePso(new ComputePipelineStateDescription {
            RootSignature = cullRootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute,
                EmbeddedShaderSource.ReadHlsl("InstanceCull.hlsl"), "CSMain", "InstanceCull.hlsl"),
        }, "InstanceCull.Cull");

        // --- Draw root sig: root const b0 (MaterialId) + CBV b1 (motion) + CBV b2 (view) + SRV t0 (Instances) +
        //     SRV t1 (VisibleIndices) + SRV t2 (GpuMaterials) + bindless (PS reads material textures). ---
        var drawParams = new[] {
            new RootParameter1(new RootConstants(0, 0, 1), ShaderVisibility.Vertex),                                     // b0 MaterialId
            new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.All),   // b1 motion
            new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(2, 0), ShaderVisibility.Vertex),// b2 view
            new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.Vertex),// t0 Instances
            new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.Vertex),// t1 VisibleIndices
            new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(2, 0), ShaderVisibility.Pixel), // t2 GpuMaterials
        };
        var wrap = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 16,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        drawRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(
                RootSignatureFlags.AllowInputAssemblerInputLayout |
                RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
                drawParams, new[] { wrap })));

        string drawHlsl = EmbeddedShaderSource.ReadHlsl("InstanceGBuffer.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, drawHlsl, "VSMain", "InstanceGBuffer.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, drawHlsl, "PSMain", "InstanceGBuffer.hlsl");
        var layout = new InputLayoutDescription(
            new InputElementDescription("POSITION", 0, Vortice.DXGI.Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Vortice.DXGI.Format.R32G32B32_Float, 0, 1),
            new InputElementDescription("TEXCOORD", 0, Vortice.DXGI.Format.R32G32_Float, 0, 2),
            new InputElementDescription("TANGENT", 0, Vortice.DXGI.Format.R32G32B32A32_Float, 0, 3));
        drawPso = dev.CreateGraphicsPso(new GraphicsPipelineStateDescription {
            RootSignature = drawRootSig, VertexShader = vs, PixelShader = ps, InputLayout = layout,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullClockwise,   // back-face cull (matches the CPU GBuffer PSO)
            BlendState = BlendDescription.Opaque, DepthStencilState = DepthStencilDescription.Default,
            RenderTargetFormats = Dx12GBuffer.ColorFormats,
            DepthStencilFormat = Dx12GBuffer.DepthFormat, SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
        }, "InstanceCull.Draw");

        // Command signature: a single DrawIndexed indirect arg (no root const in the stream — MaterialId is set
        // once per call on the CPU, identical for all instances). The cull writes the InstanceCount field.
        var argDraw = new IndirectArgumentDescription { Type = IndirectArgumentType.DrawIndexed };
        cmdSig = dev.Device.CreateCommandSignature<ID3D12CommandSignature>(
            new CommandSignatureDescription(
                System.Runtime.InteropServices.Marshal.SizeOf<DrawArgs>(), new[] { argDraw }), null);
    }

    unsafe void AllocateFixedBuffers() {
        // DrawArgs: a DEFAULT buffer the cull atomically bumps (InterlockedAdd on an UPLOAD buffer is not portable).
        // One slot per call, N-buffered. Seeded each call from drawArgsSeed (UPLOAD) via CopyBufferRegion so the
        // index count etc. are correct + InstanceCount starts at 0. Sized for FramesInFlight*MaxCallsPerFrame slots.
        int argStride = System.Runtime.InteropServices.Marshal.SizeOf<DrawArgs>();
        drawArgsFrameStride = (long)argStride * MaxCallsPerFrame;
        ulong argTotal = (ulong)(drawArgsFrameStride * dev.FramesInFlight);
        drawArgs = dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(argTotal, ResourceFlags.AllowUnorderedAccess), ResourceStates.IndirectArgument);
        drawArgs.Name = "InstanceCull.DrawArgs";
        drawArgsState = ResourceStates.IndirectArgument;
        drawArgsSeed = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(argTotal), ResourceStates.GenericRead);
        drawArgsSeedMapped = drawArgsSeed.Map<byte>(0);

        cullParamFrameStride = (long)cullParamSlotSize * MaxCallsPerFrame;
        cullParamCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(cullParamFrameStride * dev.FramesInFlight)), ResourceStates.GenericRead);
        cullParamMapped = cullParamCb.Map<byte>(0);

        viewCbFrameStride = (long)viewCbSlotSize * MaxCallsPerFrame;
        viewCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(viewCbFrameStride * dev.FramesInFlight)), ResourceStates.GenericRead);
        viewCbMapped = viewCb.Map<byte>(0);
    }

    // Grow the per-instance UPLOAD buffer + the VisibleIndices UAV to hold `count` instances (in ONE call). The
    // instances buffer is N-buffered (FrameSlot stride) like every other per-frame upload.
    unsafe void EnsureCapacity(int count) {
        if (instances != null && count <= capacity) return;
        int newCap = Math.Max(count, capacity == 0 ? 4096 : capacity * 2);
        // Defer-release the old buffers (a prior frame may still read them under overlap).
        if (instances != null) { instances.Unmap(0); dev.DeferredRelease(instances); }
        if (visibleIndices != null) dev.DeferredRelease(visibleIndices);
        capacity = newCap;
        instancesFrameStride = (long)instanceStride * capacity;
        instances = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(instancesFrameStride * dev.FramesInFlight)), ResourceStates.GenericRead);
        instances.Name = "InstanceCull.Instances";
        instancesMapped = instances.Map<byte>(0);
        // VisibleIndices is a DEFAULT UAV (cull writes survivor indices). One slot per instance is the worst case
        // (nothing culled). Not N-buffered: it's GPU-written then GPU-read in the SAME command list (a UAV->SRV
        // barrier orders them), so a later frame's cull overwrite can't race this frame's draw read.
        visibleIndices = dev.CreateUavBuffer<uint>(new uint[capacity], ResourceStates.NonPixelShaderResource);
        visibleIndices.Name = "InstanceCull.VisibleIndices";
    }

    // Called once per frame before any RenderInstanced — resets the per-call slot counter.
    public void BeginFrame() { callsThisFrame = 0; LastVisibleUpperBound = 0; }

    // Cull + indirect-draw ONE instanced mesh+material into the geometry command list (G-buffer MRT + viewport
    // already bound). `frustumPlanes` are the SAME 6 normalized planes the CPU per-submesh cull uses (from the
    // unjittered viewProj). The Hi-Z fields mirror the GPU-driven occlusion test. `materialId` is resolved by
    // the caller into the shared bindless table; `materialsGpu` is that table's GPU address. Returns survivors'
    // pre-cull upper bound is accumulated in LastVisibleUpperBound. Does nothing (returns false) on overflow.
    public unsafe bool RenderInstanced(ID3D12GraphicsCommandList4 cl, Mesh mesh, int subMeshIndex, int materialId,
        Matrix4x4[] transforms, Matrix4x4 viewProj, Vector4[] frustumPlanes,
        Matrix4x4 viewProjUnjittered, Matrix4x4 view, float near, float far,
        ulong motionCbGpu, ulong materialsGpu, int hizBindlessIndex, bool hizOn) {
        int n = transforms?.Length ?? 0;
        if (n <= 0 || materialId < 0 || callsThisFrame >= MaxCallsPerFrame) return false;
        var vb = mesh.VertexBuffer as Dx12Buffer<GLVector3>;
        var ib = mesh.IndexBuffer as Dx12IndexBuffer;
        var nb = mesh.NormalBuffer as Dx12Buffer<GLVector3>;
        var ub = mesh.UvBuffer as Dx12Buffer<Vector2>;
        var tb = mesh.TangentBuffer as Dx12Buffer<Vector4>;
        if (vb?.Resource is null || ib?.Resource is null || nb?.Resource is null ||
            ub?.Resource is null || tb?.Resource is null) return false;

        int sub = subMeshIndex >= 0 ? subMeshIndex : 0;
        if ((uint)sub >= (uint)mesh.SubMeshes.Length) return false;
        SubMeshData sm = mesh.SubMeshes[sub];
        if (sm.IndexCount <= 0) return false;
        mesh.GetSubMeshBounds(sub, out GLVector3 lmin, out GLVector3 lmax);

        EnsureCapacity(n);
        int call = callsThisFrame++;
        long instSlot = (long)dev.FrameSlot * instancesFrameStride;
        long argSlot = (long)dev.FrameSlot * drawArgsFrameStride + (long)call * System.Runtime.InteropServices.Marshal.SizeOf<DrawArgs>();
        long cpSlot = (long)dev.FrameSlot * cullParamFrameStride + (long)call * cullParamSlotSize;
        long vwSlot = (long)dev.FrameSlot * viewCbFrameStride + (long)call * viewCbSlotSize;

        // Fill the instance data: Model stored TRANSPOSED (mul(v,M) VS convention, matches PerDraw.Model) + the
        // mesh LOCAL AABB (the cull transforms it per-instance with the same 8-corner loop as the CPU WorldAabb).
        var aMin = new Vector4(lmin.X, lmin.Y, lmin.Z, 0);
        var aMax = new Vector4(lmax.X, lmax.Y, lmax.Z, 0);
        for (int i = 0; i < n; i++) {
            *(InstanceData*)(instancesMapped + instSlot + (long)i * instanceStride) = new InstanceData {
                Model = Matrix4x4.Transpose(transforms[i]), AabbMin = aMin, AabbMax = aMax,
            };
        }
        LastVisibleUpperBound += n;

        // Seed DrawArgs into the UPLOAD staging slot: index count etc. fixed; InstanceCount = 0 (the cull bumps it).
        *(DrawArgs*)(drawArgsSeedMapped + argSlot) = new DrawArgs {
            IndexCount = (uint)sm.IndexCount, InstanceCount = 0,
            StartIndex = (uint)sm.IndexStart, BaseVertex = 0, StartInstance = 0,
        };

        // Cull params (planes + Hi-Z). Hi-Z is enabled only when the pyramid is primed this frame + the slot is valid.
        bool hizEnabled = hizOn && hizBindlessIndex >= 0;
        *(InstCullParams*)(cullParamMapped + cpSlot) = new InstCullParams {
            P0 = frustumPlanes[0], P1 = frustumPlanes[1], P2 = frustumPlanes[2],
            P3 = frustumPlanes[3], P4 = frustumPlanes[4], P5 = frustumPlanes[5],
            InstanceCount = (uint)n, HizEnabled = hizEnabled ? 1u : 0u,
            HizIndex = (uint)Math.Max(hizBindlessIndex, 0), Pad0 = 0,
            ViewProj = Matrix4x4.Transpose(viewProjUnjittered), View = Matrix4x4.Transpose(view),
            // Hi-Z pyramid dims come from the shared pyramid (the caller sets them via SetHizDims each frame).
            HizParams = new Vector4(hizW, hizH, hizMips, near), HizFar = new Vector4(far, 0, 0, 0),
        };

        // View CB (b2): the jittered viewProj used as the on-screen MVP factor (transposed for mul(v,M)).
        *(Matrix4x4*)(viewCbMapped + vwSlot) = Matrix4x4.Transpose(viewProj);

        ulong instGpu = instances.GPUVirtualAddress + (ulong)instSlot;

        // 1) Seed DrawArgs (IndirectArgument/NonPixelShaderResource -> CopyDest -> copy the staged args in -> UAV).
        if (drawArgsState != ResourceStates.CopyDest) {
            cl.ResourceBarrierTransition(drawArgs, drawArgsState, ResourceStates.CopyDest);
            drawArgsState = ResourceStates.CopyDest;
        }
        cl.CopyBufferRegion(drawArgs, (ulong)argSlot, drawArgsSeed, (ulong)argSlot,
            (ulong)System.Runtime.InteropServices.Marshal.SizeOf<DrawArgs>());
        cl.ResourceBarrierTransition(drawArgs, ResourceStates.CopyDest, ResourceStates.UnorderedAccess);
        drawArgsState = ResourceStates.UnorderedAccess;

        // 2) Cull dispatch — writes VisibleIndices + atomically bumps DrawArgs.InstanceCount.
        cl.ResourceBarrierTransition(visibleIndices, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
        cl.SetDescriptorHeaps(Dx12Backend.BindlessHeap.Heap);   // GOTCHA: SetDescriptorHeaps BEFORE the root sig (bindless Hi-Z)
        cl.SetComputeRootSignature(cullRootSig);
        cl.SetPipelineState(cullPso);
        cl.SetComputeRootConstantBufferView(0, cullParamCb.GPUVirtualAddress + (ulong)cpSlot);
        cl.SetComputeRootShaderResourceView(1, instGpu);
        cl.SetComputeRootUnorderedAccessView(2, visibleIndices.GPUVirtualAddress);
        cl.SetComputeRootUnorderedAccessView(3, drawArgs.GPUVirtualAddress + (ulong)argSlot);
        cl.Dispatch((uint)((n + 63) / 64), 1, 1);

        // 3) Barrier VisibleIndices for the VS read; DrawArgs UAV -> IndirectArgument for the indirect draw.
        cl.ResourceBarrierTransition(visibleIndices, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
        cl.ResourceBarrierTransition(drawArgs, ResourceStates.UnorderedAccess, ResourceStates.IndirectArgument);
        drawArgsState = ResourceStates.IndirectArgument;

        // 4) Indirect instanced draw — InstanceCount comes from the cull's atomic counter in DrawArgs.
        cl.SetDescriptorHeaps(Dx12Backend.BindlessHeap.Heap);   // GOTCHA: before the root sig
        cl.SetGraphicsRootSignature(drawRootSig);
        cl.SetPipelineState(drawPso);
        cl.SetGraphicsRoot32BitConstant(0, (uint)materialId, 0);                                  // b0 MaterialId
        cl.SetGraphicsRootConstantBufferView(1, motionCbGpu);                                     // b1 motion
        cl.SetGraphicsRootConstantBufferView(2, viewCb.GPUVirtualAddress + (ulong)vwSlot);        // b2 view
        cl.SetGraphicsRootShaderResourceView(3, instGpu);                                         // t0 Instances
        cl.SetGraphicsRootShaderResourceView(4, visibleIndices.GPUVirtualAddress);                // t1 VisibleIndices
        cl.SetGraphicsRootShaderResourceView(5, materialsGpu);                                    // t2 GpuMaterials
        cl.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
        Span<VertexBufferView> vbViews = stackalloc VertexBufferView[4];
        vbViews[0] = new VertexBufferView(vb.GpuAddress, (uint)vb.ByteSize, (uint)vb.Stride);
        vbViews[1] = new VertexBufferView(nb.GpuAddress, (uint)nb.ByteSize, (uint)nb.Stride);
        vbViews[2] = new VertexBufferView(ub.GpuAddress, (uint)ub.ByteSize, (uint)ub.Stride);
        vbViews[3] = new VertexBufferView(tb.GpuAddress, (uint)tb.ByteSize, (uint)tb.Stride);
        cl.IASetVertexBuffers(0, vbViews);
        cl.IASetIndexBuffer(new IndexBufferView(ib.GpuAddress, (uint)ib.ByteSize, Vortice.DXGI.Format.R32_UInt));
        cl.ExecuteIndirect(cmdSig, 1, drawArgs, (ulong)argSlot, null, 0);
        return true;
    }

    // The shared Hi-Z pyramid dimensions (the caller sets these from gpuDriven's pyramid each frame so the
    // cull's screen projection matches). Width/height/mips; near/far come per-call.
    float hizW, hizH, hizMips;
    public void SetHizDims(int width, int height, int mips) { hizW = width; hizH = height; hizMips = mips; }

    public unsafe void Dispose() {
        cullRootSig?.Dispose(); cullPso?.Dispose();
        drawRootSig?.Dispose(); drawPso?.Dispose(); cmdSig?.Dispose();
        if (instances != null) instances.Unmap(0);
        instances?.Dispose(); visibleIndices?.Dispose();
        drawArgs?.Dispose(); drawArgsSeed?.Dispose(); cullParamCb?.Dispose(); viewCb?.Dispose();
    }
}
