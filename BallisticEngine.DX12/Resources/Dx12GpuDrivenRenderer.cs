using System;
using System.Collections.Generic;
using System.Numerics;
using Vortice.Direct3D12;
using Vortice.Dxc;
using GLMatrix4 = OpenTK.Mathematics.Matrix4;
using GLVector3 = OpenTK.Mathematics.Vector3;

namespace BallisticEngine.DX12;

// GPU-driven geometry pass for the DX12 clustered-deferred renderer (port of OpenGL/Rendering/GpuDriven/).
// Targets WHOLE-MESH renderers (SubMeshIndex < 0): a compute shader frustum-culls all their opaque
// submeshes (GpuCull.hlsl — bit-identical to the CPU AabbInFrustum, so the visible set matches), compacts
// the survivors into an ExecuteIndirect command list + a per-draw buffer, and a single ExecuteIndirect per
// mesh draws them all with BINDLESS materials (GBufferBindless.hlsl) — collapsing ~1600 CPU DrawIndexed
// calls (+ per-draw CBV/descriptor binds) into a handful of dispatch+ExecuteIndirect calls. Byte-identical
// G-buffer to the CPU path (the cull + shading math match exactly). Gated by BALLISTIC_DX12_GPUDRIVEN.
public sealed class Dx12GpuDrivenRenderer : IDisposable {
    const int Capacity = 8192;     // max whole-mesh submeshes per frame
    const int MaxGroups = 64;      // distinct meshes among whole-mesh renderers
    const int DrawCmdStride = 24;  // GpuDrawCommand bytes (1 root const + 5 DrawIndexedArguments)

    readonly Dx12Device dev;

    // Cull (compute): CullParams CBV(b0) + Metas SRV(t0) + Commands/Counter/PerDraws UAV(u0/u1/u2).
    ID3D12RootSignature cullRootSig;
    ID3D12PipelineState cullPso;
    // GPU-driven G-buffer draw: DrawIndex root const(b0) + PerDraws SRV(t0) + GpuMaterials SRV(t1) + bindless.
    ID3D12RootSignature drawRootSig;
    ID3D12PipelineState drawPso;
    ID3D12CommandSignature cmdSig;

    ID3D12Resource metaUpload;      unsafe byte* metaMapped;        // SubmeshMeta[] (rebuilt per frame)
    ID3D12Resource cullParamUpload; unsafe byte* cullParamMapped;   // CullParams[MaxGroups] (256B slots)
    ID3D12Resource commands;        // DEFAULT UAV — indirect draw commands (one slot per submesh, in order)
    ID3D12Resource perDraws;        // DEFAULT UAV — per-draw Mvp/Model/MaterialId
    ID3D12Resource materials;       unsafe byte* materialsMapped;   // GpuMaterial[] (built on material change)
    int cullParamSlotSize;
    int metaStride, perDrawStride, materialStride;
    public long LastTris;        // triangles fed to the GPU cull this frame (pre-cull upper bound, for stats)
    public int LastSubmeshes;    // submeshes fed to the GPU cull this frame

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct SubmeshMeta {
        public Matrix4x4 Mvp; public Matrix4x4 Model;
        public Vector4 AabbMin; public Vector4 AabbMax;
        public uint FirstIndex, IndexCount, MaterialId, Flags;
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct PerDraw { public Matrix4x4 Mvp; public Matrix4x4 Model; public uint MaterialId; public uint P0, P1, P2; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct GpuMaterial {
        public uint DiffuseIdx, NormalIdx, MetallicIdx, RoughnessIdx;
        public uint AoIdx, EmissiveIdx, Pad0, Pad1;
        public Vector4 BaseColorFactor; public Vector4 EmissiveFactor;
        public float Metallic, Roughness, SpecularReflectance, NormalStrength;
        public float NormalFlipY, HasMetallicMap, HasRoughnessMap, PackedOrm;
        public float Cutout, HasEmissive, Pad2, Pad3;
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct CullParams { public Vector4 P0, P1, P2, P3, P4, P5; public uint SubmeshCount, OutBase, Pad0, Pad1; }

    // Material table cache (rebuilt when the material set changes).
    readonly Dictionary<Material, int> materialIds = new();
    readonly Dictionary<Dx12Texture2D, int> bindlessIds = new();
    int materialCount;
    int tableStamp = -1;

    // --- GPU-driven sun shadows (depth-only, per cascade) ---
    const int ShadowCascades = 4;
    const int ShadowCapacity = ShadowCascades * Capacity;
    ID3D12PipelineState shadowCullPso;       // reuses cullRootSig (CBV b0 + SRV t0 + UAV u0/u1)
    ID3D12RootSignature shadowDrawRootSig;   // root const b0 (DrawIndex) + SRV t0 (ShadowPerDraws)
    ID3D12PipelineState shadowDrawPso;       // depth-only, slope bias (matches CPU shadow PSO)
    ID3D12CommandSignature shadowCmdSig;
    ID3D12Resource shadowMetaUpload;   unsafe byte* shadowMetaMapped;
    ID3D12Resource shadowCullParamUpload; unsafe byte* shadowCullParamMapped;
    ID3D12Resource shadowCommands;     // DEFAULT UAV
    ID3D12Resource shadowPerDraws;     // DEFAULT UAV
    int shadowMetaStride;
    readonly List<(int cascade, Dx12Buffer<GLVector3> vb, Dx12IndexBuffer ib, int baseIdx, int count)> shadowSlices = new();

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct ShadowMeta { public Matrix4x4 LightMvp; public Vector4 AabbMin, AabbMax; public uint FirstIndex, IndexCount, Pad0, Pad1; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct ShadowPerDraw { public Matrix4x4 LightMvp; }

    public Dx12GpuDrivenRenderer(Dx12Device device) {
        dev = device;
        metaStride = System.Runtime.InteropServices.Marshal.SizeOf<SubmeshMeta>();
        perDrawStride = System.Runtime.InteropServices.Marshal.SizeOf<PerDraw>();
        materialStride = System.Runtime.InteropServices.Marshal.SizeOf<GpuMaterial>();
        shadowMetaStride = System.Runtime.InteropServices.Marshal.SizeOf<ShadowMeta>();
        BuildPipelines();
        AllocateBuffers();
        BuildShadowPipelines();
        AllocateShadowBuffers();
    }

    unsafe void BuildPipelines() {
        // --- Cull root sig: CBV b0 + SRV t0 (Metas) + UAV u0 (Commands) + UAV u1 (PerDraws) ---
        var cullParams = new[] {
            new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All),
            new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All),
            new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(0, 0), ShaderVisibility.All),
            new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(1, 0), ShaderVisibility.All),
        };
        cullRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, cullParams)));
        string cullHlsl = EmbeddedShaderSource.ReadHlsl("GpuCull.hlsl");
        cullPso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription {
            RootSignature = cullRootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, cullHlsl, "CSMain", "GpuCull.hlsl"),
        });

        // --- Draw root sig: root const b0 (DrawIndex) + SRV t0 (PerDraws) + SRV t1 (GpuMaterials) + bindless ---
        var drawParams = new[] {
            new RootParameter1(new RootConstants(0, 0, 1), ShaderVisibility.Vertex),
            new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.Vertex),
            new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.Pixel),
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

        string drawHlsl = EmbeddedShaderSource.ReadHlsl("GBufferBindless.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, drawHlsl, "VSMain", "GBufferBindless.hlsl");
        byte[] ps = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, drawHlsl, "PSMain", "GBufferBindless.hlsl");
        var layout = new InputLayoutDescription(
            new InputElementDescription("POSITION", 0, Vortice.DXGI.Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Vortice.DXGI.Format.R32G32B32_Float, 0, 1),
            new InputElementDescription("TEXCOORD", 0, Vortice.DXGI.Format.R32G32_Float, 0, 2),
            new InputElementDescription("TANGENT", 0, Vortice.DXGI.Format.R32G32B32A32_Float, 0, 3));
        drawPso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = drawRootSig, VertexShader = vs, PixelShader = ps, InputLayout = layout,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullClockwise,   // back-face cull (matches CPU GBuffer PSO)
            BlendState = BlendDescription.Opaque, DepthStencilState = DepthStencilDescription.Default,
            RenderTargetFormats = Dx12GBuffer.ColorFormats,
            DepthStencilFormat = Dx12GBuffer.DepthFormat, SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
        });

        // Command signature: [Constant -> root param 0][DrawIndexed]. References drawRootSig (root const).
        var argConstant = new IndirectArgumentDescription { Type = IndirectArgumentType.Constant };
        argConstant.Constant.RootParameterIndex = 0;
        argConstant.Constant.DestOffsetIn32BitValues = 0;
        argConstant.Constant.Num32BitValuesToSet = 1;
        var argDraw = new IndirectArgumentDescription { Type = IndirectArgumentType.DrawIndexed };
        cmdSig = dev.Device.CreateCommandSignature<ID3D12CommandSignature>(
            new CommandSignatureDescription(DrawCmdStride, new[] { argConstant, argDraw }), drawRootSig);
    }

    unsafe void AllocateBuffers() {
        var zeroPerDraw = new PerDraw[Capacity];
        var zeroCmds = new byte[Capacity * DrawCmdStride];

        commands = dev.CreateUavBuffer<byte>(zeroCmds, ResourceStates.IndirectArgument);
        perDraws = dev.CreateUavBuffer<PerDraw>(zeroPerDraw, ResourceStates.NonPixelShaderResource);

        metaUpload = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(metaStride * Capacity)), ResourceStates.GenericRead);
        metaMapped = metaUpload.Map<byte>(0);

        cullParamSlotSize = (System.Runtime.InteropServices.Marshal.SizeOf<CullParams>() + 255) & ~255;
        cullParamUpload = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(cullParamSlotSize * MaxGroups)), ResourceStates.GenericRead);
        cullParamMapped = cullParamUpload.Map<byte>(0);

        materials = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(materialStride * 4096)), ResourceStates.GenericRead);
        materialsMapped = materials.Map<byte>(0);
    }

    // Build / rebuild the bindless material table from the whole-mesh renderers' opaque submeshes. Cached
    // by a stamp (material-set size); rebuild resets the shared bindless heap + caches.
    public unsafe void EnsureMaterialTable(List<IStaticMeshRenderer> wholeMesh) {
        // Stamp = renderer count + total submesh count (cheap change detector for a static scene).
        int stamp = wholeMesh.Count;
        foreach (var r in wholeMesh) { var m = r.SharedMesh; if (m != null) stamp = stamp * 31 + m.SubMeshes.Length; }
        if (stamp == tableStamp) return;
        tableStamp = stamp;

        Dx12Backend.BindlessHeap.Reset();
        materialIds.Clear();
        bindlessIds.Clear();
        materialCount = 0;

        foreach (var r in wholeMesh) {
            Mesh mesh = r.SharedMesh; if (mesh is null) continue;
            for (int s = 0; s < mesh.SubMeshes.Length; s++) {
                Material mat = r.MaterialFor(s);
                if (mat is null || mat.Transparent || materialIds.ContainsKey(mat)) continue;
                int id = materialCount++;
                materialIds[mat] = id;
                bool hasMetal = mat.Metallic is not null, hasRough = mat.Roughness is not null;
                var gm = new GpuMaterial {
                    DiffuseIdx = (uint)Bindless(mat.Diffuse, TextureType.Diffuse),
                    NormalIdx = (uint)Bindless(mat.Normal, TextureType.Normal),
                    MetallicIdx = (uint)Bindless(mat.Metallic, TextureType.Metallic),
                    RoughnessIdx = (uint)Bindless(mat.Roughness, TextureType.Roughness),
                    AoIdx = (uint)Bindless(mat.AO, TextureType.AO),
                    EmissiveIdx = (uint)Bindless(mat.Emissive, TextureType.Emissive),
                    BaseColorFactor = ToNum(mat.BaseColorFactor),
                    EmissiveFactor = new Vector4(mat.EmissiveColor.X, mat.EmissiveColor.Y, mat.EmissiveColor.Z, 0) * mat.EmissiveIntensity,
                    Metallic = mat.MetallicFactor, Roughness = mat.RoughnessFactor,
                    SpecularReflectance = mat.SpecularReflectance, NormalStrength = mat.NormalStrength,
                    NormalFlipY = mat.NormalFlipY ? 1f : 0f, HasMetallicMap = hasMetal ? 1f : 0f,
                    HasRoughnessMap = hasRough ? 1f : 0f, PackedOrm = mat.PackedOrm ? 1f : 0f,
                    Cutout = mat.Cutout ? 1f : 0f, HasEmissive = mat.IsEmissive ? 1f : 0f,
                };
                *(GpuMaterial*)(materialsMapped + (long)id * materialStride) = gm;
            }
        }
    }

    // Resolve a material map to a bindless index (real texture or the neutral fallback — matches CPU BindSrv).
    int Bindless(Texture2D tex, TextureType type) {
        var dx = (tex as Dx12Texture2D) ?? (DefaultTextures.Neutral(type) as Dx12Texture2D);
        if (dx is null) return 0;
        if (bindlessIds.TryGetValue(dx, out int idx)) return idx;
        idx = Dx12Backend.RegisterBindless(dx.SrvCpu);
        bindlessIds[dx] = idx;
        return idx;
    }

    // Record the cull + ExecuteIndirect for all whole-mesh groups into the geometry command list (which has
    // the G-buffer MRT + viewport already bound). `frustumPlanes` are the SAME 6 normalized planes the CPU
    // cull uses (from the unjittered viewProj). Returns the submesh draw upper-bound for stats.
    public unsafe int RenderInto(ID3D12GraphicsCommandList4 cl, List<IStaticMeshRenderer> wholeMesh,
                                 Matrix4x4 viewProj, Vector4[] frustumPlanes) {
        // Group by mesh; build the flat SubmeshMeta array (per frame: Mvp depends on the camera).
        var groups = new List<(Dx12Buffer<GLVector3> vb, Dx12Buffer<GLVector3> nb,
            Dx12Buffer<OpenTK.Mathematics.Vector2> ub, Dx12Buffer<OpenTK.Mathematics.Vector4> tb,
            Dx12IndexBuffer ib, int baseIdx, int count)>();
        var byMesh = new Dictionary<Mesh, List<IStaticMeshRenderer>>();
        foreach (var r in wholeMesh) {
            Mesh m = r.SharedMesh; if (m is null) continue;
            if (!byMesh.TryGetValue(m, out var list)) { list = new(); byMesh[m] = list; }
            list.Add(r);
        }

        int total = 0, groupCount = 0;
        long triAccum = 0;
        foreach (var kv in byMesh) {
            if (groupCount >= MaxGroups) break;
            Mesh mesh = kv.Key;
            var vb = mesh.VertexBuffer as Dx12Buffer<GLVector3>;
            var ib = mesh.IndexBuffer as Dx12IndexBuffer;
            var nb = mesh.NormalBuffer as Dx12Buffer<GLVector3>;
            var ub = mesh.UvBuffer as Dx12Buffer<OpenTK.Mathematics.Vector2>;
            var tb = mesh.TangentBuffer as Dx12Buffer<OpenTK.Mathematics.Vector4>;
            if (vb?.Resource is null || ib?.Resource is null || nb?.Resource is null ||
                ub?.Resource is null || tb?.Resource is null) continue;

            int groupBase = total;
            foreach (var r in kv.Value) {
                Matrix4x4 model = ToNum(r.Transform.WorldMatrix);
                Matrix4x4 mvp = model * viewProj;
                for (int s = 0; s < mesh.SubMeshes.Length && total < Capacity; s++) {
                    SubMeshData sub = mesh.SubMeshes[s];
                    if (sub.IndexCount <= 0) continue;
                    Material mat = r.MaterialFor(s);
                    if (mat is null || mat.Transparent) continue;   // transparents -> forward pass
                    if (!materialIds.TryGetValue(mat, out int matId)) continue;
                    mesh.GetSubMeshBounds(s, out GLVector3 lmin, out GLVector3 lmax);
                    WorldAabb(lmin, lmax, model, out Vector3 wlo, out Vector3 whi);
                    *(SubmeshMeta*)(metaMapped + (long)total * metaStride) = new SubmeshMeta {
                        Mvp = Matrix4x4.Transpose(mvp), Model = Matrix4x4.Transpose(model),
                        AabbMin = new Vector4(wlo, 0), AabbMax = new Vector4(whi, 0),
                        FirstIndex = (uint)sub.IndexStart, IndexCount = (uint)sub.IndexCount,
                        MaterialId = (uint)matId, Flags = 0,
                    };
                    triAccum += sub.IndexCount / 3;
                    total++;
                }
            }
            int groupTotal = total - groupBase;
            if (groupTotal == 0) continue;
            // Fill this group's CullParams slot.
            var cp = new CullParams {
                P0 = frustumPlanes[0], P1 = frustumPlanes[1], P2 = frustumPlanes[2],
                P3 = frustumPlanes[3], P4 = frustumPlanes[4], P5 = frustumPlanes[5],
                SubmeshCount = (uint)groupTotal, OutBase = (uint)groupBase,
            };
            *(CullParams*)(cullParamMapped + (long)groupCount * cullParamSlotSize) = cp;
            groups.Add((vb, nb, ub, tb, ib, groupBase, groupTotal));
            groupCount++;
        }
        if (groups.Count == 0) return 0;

        // 1) Transition outputs back to UAV (from last frame's draw states).
        cl.ResourceBarrierTransition(commands, ResourceStates.IndirectArgument, ResourceStates.UnorderedAccess);
        cl.ResourceBarrierTransition(perDraws, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);

        // 2) Cull dispatch per group (writes Commands + PerDraws; one slot per submesh, in order).
        cl.SetComputeRootSignature(cullRootSig);
        cl.SetPipelineState(cullPso);
        cl.SetComputeRootShaderResourceView(1, metaUpload.GPUVirtualAddress);
        cl.SetComputeRootUnorderedAccessView(2, commands.GPUVirtualAddress);
        cl.SetComputeRootUnorderedAccessView(3, perDraws.GPUVirtualAddress);
        for (int g = 0; g < groups.Count; g++) {
            cl.SetComputeRootConstantBufferView(0, cullParamUpload.GPUVirtualAddress + (ulong)((long)g * cullParamSlotSize));
            cl.Dispatch((uint)((groups[g].count + 63) / 64), 1, 1);
        }

        // 3) Barrier outputs for the draw (transition flushes the cull's UAV writes).
        cl.ResourceBarrierTransition(commands, ResourceStates.UnorderedAccess, ResourceStates.IndirectArgument);
        cl.ResourceBarrierTransition(perDraws, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);

        // 4) Bindless GPU-driven draw — one ExecuteIndirect per mesh group.
        cl.SetDescriptorHeaps(Dx12Backend.BindlessHeap.Heap);   // GOTCHA: before the root sig
        cl.SetGraphicsRootSignature(drawRootSig);
        cl.SetPipelineState(drawPso);
        cl.SetGraphicsRootShaderResourceView(1, perDraws.GPUVirtualAddress);
        cl.SetGraphicsRootShaderResourceView(2, materials.GPUVirtualAddress);
        cl.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
        for (int g = 0; g < groups.Count; g++) {
            var gp = groups[g];
            Span<VertexBufferView> vbViews = stackalloc VertexBufferView[4];
            vbViews[0] = new VertexBufferView(gp.vb.GpuAddress, (uint)gp.vb.ByteSize, (uint)gp.vb.Stride);
            vbViews[1] = new VertexBufferView(gp.nb.GpuAddress, (uint)gp.nb.ByteSize, (uint)gp.nb.Stride);
            vbViews[2] = new VertexBufferView(gp.ub.GpuAddress, (uint)gp.ub.ByteSize, (uint)gp.ub.Stride);
            vbViews[3] = new VertexBufferView(gp.tb.GpuAddress, (uint)gp.tb.ByteSize, (uint)gp.tb.Stride);
            cl.IASetVertexBuffers(0, vbViews);
            cl.IASetIndexBuffer(new IndexBufferView(gp.ib.GpuAddress, (uint)gp.ib.ByteSize, Vortice.DXGI.Format.R32_UInt));
            // No count buffer: issue exactly the group's submesh commands in order; culled ones have
            // InstanceCount 0 (GPU skips them). Order-preserving -> byte-identical to the CPU path.
            cl.ExecuteIndirect(cmdSig, (uint)gp.count, commands, (ulong)((long)gp.baseIdx * DrawCmdStride), null, 0);
        }
        LastTris = triAccum;
        LastSubmeshes = total;
        return groups.Count;   // ExecuteIndirect calls submitted (the CPU-submit win: ~1600 draws -> a few)
    }

    unsafe void BuildShadowPipelines() {
        string cullHlsl = EmbeddedShaderSource.ReadHlsl("GpuCullShadow.hlsl");
        shadowCullPso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription {
            RootSignature = cullRootSig,   // same layout: CBV b0 + SRV t0 + UAV u0/u1
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, cullHlsl, "CSMain", "GpuCullShadow.hlsl"),
        });

        // Draw root sig: root const b0 (DrawIndex) + SRV t0 (ShadowPerDraws). Depth-only, no samplers.
        var drawParams = new[] {
            new RootParameter1(new RootConstants(0, 0, 1), ShaderVisibility.Vertex),
            new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.Vertex),
        };
        shadowDrawRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout, drawParams)));

        string drawHlsl = EmbeddedShaderSource.ReadHlsl("ShadowDepthIndirect.hlsl");
        byte[] vs = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, drawHlsl, "VSMain", "ShadowDepthIndirect.hlsl");
        var layout = new InputLayoutDescription(
            new InputElementDescription("POSITION", 0, Vortice.DXGI.Format.R32G32B32_Float, 0, 0));
        var raster = RasterizerDescription.CullClockwise;     // matches the CPU shadow PSO bias exactly
        raster.DepthBias = 2000; raster.SlopeScaledDepthBias = 2.5f; raster.DepthBiasClamp = 0f;
        shadowDrawPso = dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = shadowDrawRootSig, VertexShader = vs, PixelShader = default, InputLayout = layout,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = raster, BlendState = BlendDescription.Opaque, DepthStencilState = DepthStencilDescription.Default,
            RenderTargetFormats = Array.Empty<Vortice.DXGI.Format>(),
            DepthStencilFormat = Dx12ShadowMap.DepthFormat, SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
        });

        var argConstant = new IndirectArgumentDescription { Type = IndirectArgumentType.Constant };
        argConstant.Constant.RootParameterIndex = 0;
        argConstant.Constant.DestOffsetIn32BitValues = 0;
        argConstant.Constant.Num32BitValuesToSet = 1;
        var argDraw = new IndirectArgumentDescription { Type = IndirectArgumentType.DrawIndexed };
        shadowCmdSig = dev.Device.CreateCommandSignature<ID3D12CommandSignature>(
            new CommandSignatureDescription(DrawCmdStride, new[] { argConstant, argDraw }), shadowDrawRootSig);
    }

    unsafe void AllocateShadowBuffers() {
        shadowCommands = dev.CreateUavBuffer<byte>(new byte[ShadowCapacity * DrawCmdStride], ResourceStates.IndirectArgument);
        shadowPerDraws = dev.CreateUavBuffer<ShadowPerDraw>(new ShadowPerDraw[ShadowCapacity], ResourceStates.NonPixelShaderResource);
        shadowMetaUpload = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)((long)shadowMetaStride * ShadowCapacity)), ResourceStates.GenericRead);
        shadowMetaMapped = shadowMetaUpload.Map<byte>(0);
        shadowCullParamUpload = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)((long)cullParamSlotSize * ShadowCascades * MaxGroups)), ResourceStates.GenericRead);
        shadowCullParamMapped = shadowCullParamUpload.Map<byte>(0);
    }

    // Build the per-cascade SubmeshMeta + dispatch the shadow culls (records into `cl`). Must run BEFORE the
    // per-cascade depth draws. Mirrors the CPU shadow caster set EXACTLY (every submesh with IndexCount > 0,
    // no material filter — depth-only) so the shadow maps are byte-identical. Stores the slices for
    // DrawShadowCascade. cascadeMatrices = the 4 light-space view*proj matrices.
    public unsafe void BuildShadowCull(ID3D12GraphicsCommandList4 cl, List<IStaticMeshRenderer> wholeMesh,
                                       Matrix4x4[] cascadeMatrices) {
        shadowSlices.Clear();
        var byMesh = new Dictionary<Mesh, List<IStaticMeshRenderer>>();
        foreach (var r in wholeMesh) {
            Mesh m = r.SharedMesh; if (m is null) continue;
            if (!byMesh.TryGetValue(m, out var list)) { list = new(); byMesh[m] = list; }
            list.Add(r);
        }

        int total = 0, sliceCount = 0;
        var planes = new Vector4[6];
        for (int c = 0; c < ShadowCascades; c++) {
            ExtractPlanes(cascadeMatrices[c], planes);
            foreach (var kv in byMesh) {
                if (sliceCount >= ShadowCascades * MaxGroups) break;
                Mesh mesh = kv.Key;
                var vb = mesh.VertexBuffer as Dx12Buffer<GLVector3>;
                var ib = mesh.IndexBuffer as Dx12IndexBuffer;
                if (vb?.Resource is null || ib?.Resource is null) continue;
                int sliceBase = total;
                foreach (var r in kv.Value) {
                    Matrix4x4 model = ToNum(r.Transform.WorldMatrix);
                    Matrix4x4 lightMvp = Matrix4x4.Transpose(model * cascadeMatrices[c]);
                    for (int s = 0; s < mesh.SubMeshes.Length && total < ShadowCapacity; s++) {
                        SubMeshData sub = mesh.SubMeshes[s];
                        if (sub.IndexCount <= 0) continue;
                        mesh.GetSubMeshBounds(s, out GLVector3 lmin, out GLVector3 lmax);
                        WorldAabb(lmin, lmax, model, out Vector3 wlo, out Vector3 whi);
                        *(ShadowMeta*)(shadowMetaMapped + (long)total * shadowMetaStride) = new ShadowMeta {
                            LightMvp = lightMvp, AabbMin = new Vector4(wlo, 0), AabbMax = new Vector4(whi, 0),
                            FirstIndex = (uint)sub.IndexStart, IndexCount = (uint)sub.IndexCount,
                        };
                        total++;
                    }
                }
                int count = total - sliceBase;
                if (count == 0) continue;
                var cp = new CullParams {
                    P0 = planes[0], P1 = planes[1], P2 = planes[2], P3 = planes[3], P4 = planes[4], P5 = planes[5],
                    SubmeshCount = (uint)count, OutBase = (uint)sliceBase,
                };
                *(CullParams*)(shadowCullParamMapped + (long)sliceCount * cullParamSlotSize) = cp;
                shadowSlices.Add((c, vb, ib, sliceBase, count));
                sliceCount++;
            }
        }
        if (shadowSlices.Count == 0) return;

        cl.ResourceBarrierTransition(shadowCommands, ResourceStates.IndirectArgument, ResourceStates.UnorderedAccess);
        cl.ResourceBarrierTransition(shadowPerDraws, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
        cl.SetComputeRootSignature(cullRootSig);
        cl.SetPipelineState(shadowCullPso);
        cl.SetComputeRootShaderResourceView(1, shadowMetaUpload.GPUVirtualAddress);
        cl.SetComputeRootUnorderedAccessView(2, shadowCommands.GPUVirtualAddress);
        cl.SetComputeRootUnorderedAccessView(3, shadowPerDraws.GPUVirtualAddress);
        for (int i = 0; i < shadowSlices.Count; i++) {
            cl.SetComputeRootConstantBufferView(0, shadowCullParamUpload.GPUVirtualAddress + (ulong)((long)i * cullParamSlotSize));
            cl.Dispatch((uint)((shadowSlices[i].count + 63) / 64), 1, 1);
        }
        cl.ResourceBarrierTransition(shadowCommands, ResourceStates.UnorderedAccess, ResourceStates.IndirectArgument);
        cl.ResourceBarrierTransition(shadowPerDraws, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
    }

    // ExecuteIndirect this cascade's shadow slices into the currently-bound cascade DSV (inside RenderCascade).
    public void DrawShadowCascade(ID3D12GraphicsCommandList4 cl, int cascade) {
        bool any = false;
        for (int i = 0; i < shadowSlices.Count; i++) if (shadowSlices[i].cascade == cascade) { any = true; break; }
        if (!any) return;
        cl.SetGraphicsRootSignature(shadowDrawRootSig);
        cl.SetPipelineState(shadowDrawPso);
        cl.SetGraphicsRootShaderResourceView(1, shadowPerDraws.GPUVirtualAddress);
        cl.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
        for (int i = 0; i < shadowSlices.Count; i++) {
            var sl = shadowSlices[i];
            if (sl.cascade != cascade) continue;
            cl.IASetVertexBuffers(0, new VertexBufferView(sl.vb.GpuAddress, (uint)sl.vb.ByteSize, (uint)sl.vb.Stride));
            cl.IASetIndexBuffer(new IndexBufferView(sl.ib.GpuAddress, (uint)sl.ib.ByteSize, Vortice.DXGI.Format.R32_UInt));
            cl.ExecuteIndirect(shadowCmdSig, (uint)sl.count, shadowCommands, (ulong)((long)sl.baseIdx * DrawCmdStride), null, 0);
        }
    }

    // Gribb-Hartmann frustum planes from a row-major view*proj — identical to DX12HDRenderer.ExtractFrustumPlanes.
    static void ExtractPlanes(Matrix4x4 m, Vector4[] p) {
        Vector4 r1 = new(m.M11, m.M21, m.M31, m.M41);
        Vector4 r2 = new(m.M12, m.M22, m.M32, m.M42);
        Vector4 r3 = new(m.M13, m.M23, m.M33, m.M43);
        Vector4 r4 = new(m.M14, m.M24, m.M34, m.M44);
        p[0] = r4 + r1; p[1] = r4 - r1; p[2] = r4 + r2; p[3] = r4 - r2; p[4] = r3; p[5] = r4 - r3;
        for (int i = 0; i < 6; i++) {
            var n = new Vector3(p[i].X, p[i].Y, p[i].Z);
            float len = n.Length();
            if (len > 1e-6f) p[i] /= len;
        }
    }

    static void WorldAabb(GLVector3 localMin, GLVector3 localMax, Matrix4x4 model, out Vector3 lo, out Vector3 hi) {
        lo = new Vector3(float.MaxValue); hi = new Vector3(float.MinValue);
        for (int c = 0; c < 8; c++) {
            var lc = new Vector3((c & 1) == 0 ? localMin.X : localMax.X,
                                 (c & 2) == 0 ? localMin.Y : localMax.Y,
                                 (c & 4) == 0 ? localMin.Z : localMax.Z);
            Vector3 w = Vector3.Transform(lc, model);
            lo = Vector3.Min(lo, w); hi = Vector3.Max(hi, w);
        }
    }

    static Matrix4x4 ToNum(GLMatrix4 m) => new(
        m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);
    static Vector4 ToNum(OpenTK.Mathematics.Vector4 v) => new(v.X, v.Y, v.Z, v.W);

    public void Dispose() {
        cullRootSig?.Dispose(); cullPso?.Dispose(); drawRootSig?.Dispose(); drawPso?.Dispose();
        cmdSig?.Dispose(); commands?.Dispose(); perDraws?.Dispose();
        metaUpload?.Dispose(); cullParamUpload?.Dispose(); materials?.Dispose();
        shadowCullPso?.Dispose(); shadowDrawRootSig?.Dispose(); shadowDrawPso?.Dispose(); shadowCmdSig?.Dispose();
        shadowCommands?.Dispose(); shadowPerDraws?.Dispose(); shadowMetaUpload?.Dispose(); shadowCullParamUpload?.Dispose();
    }
}
