using System;
using System.Collections.Generic;
using System.Numerics;
using Vortice.Direct3D12;
using Vortice.Dxc;
using GLMatrix4 = System.Numerics.Matrix4x4;   // engine math is System.Numerics now; ToNum(...) is an identity copy
using GLVector3 = System.Numerics.Vector3;

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

    // Cull (compute): CullParams CBV(b0) + Metas SRV(t0) + Commands/PerDraws UAV(u0/u1). cullRootSig is the
    // PLAIN one (shadow cull); geoCullRootSig adds a point sampler + bindless flag for the Hi-Z pyramid read.
    ID3D12RootSignature cullRootSig;
    ID3D12RootSignature geoCullRootSig;
    ID3D12PipelineState cullPso;
    // Hi-Z occlusion (camera geometry cull only).
    Dx12HiZ hiz;
    int hizBindlessIndex = -1;
    bool hizOnThisFrame;
    // GPU-driven G-buffer draw: DrawIndex root const(b0) + PerDraws SRV(t0) + GpuMaterials SRV(t1) + bindless.
    ID3D12RootSignature drawRootSig;
    ID3D12PipelineState drawPso;
    ID3D12CommandSignature cmdSig;

    ID3D12Resource metaUpload;      unsafe byte* metaMapped;        // SubmeshMeta[] (rebuilt per frame)
    ID3D12Resource cullParamUpload; unsafe byte* cullParamMapped;   // CullParams[MaxGroups] (256B slots)
    ID3D12Resource commands;        // DEFAULT UAV — indirect draw commands (one slot per submesh, in order)
    ID3D12Resource perDraws;        // DEFAULT UAV — per-draw Mvp/Model/MaterialId (GPU cull writes these)
    ID3D12Resource materials;       unsafe byte* materialsMapped;   // GpuMaterial[] (built on material change)
    // R2 — CPU per-submesh bindless: the CPU draw loop reuses drawPso/drawRootSig/GBufferBindless.hlsl + this
    // exact GpuMaterials table, but writes its OWN PerDraw entries (Mvp/Model/MaterialId) into a CPU-mapped,
    // N-buffered UPLOAD buffer (the GPU-driven `perDraws` is a DEFAULT UAV the cull writes — the CPU can't map it).
    // One draw = one entry; DrawIndex root const selects it, same as the indirect path. Capacity = MaxCpuDraws.
    ID3D12Resource cpuPerDraws;     unsafe byte* cpuPerDrawsMapped;
    long cpuPerDrawsFrameStride;    // perDrawStride*MaxCpuDraws bytes per frame slot
    const int MaxCpuDraws = 8192;   // CPU-path opaque submeshes per frame (split-import scenes); over → old path

    // R3b — compute skinning: skin pos/normal/tangent on the compute path into per-renderer transient buffers,
    // then the skinned result draws through the static GPU-driven path. Root SRV/UAV only (no descriptor heap).
    ID3D12RootSignature skinRootSig;
    ID3D12PipelineState skinPso;
    // R4 — mesh-shader meshlet pipeline (null unless dev.HasMeshShaders).
    ID3D12RootSignature meshletRootSig;
    ID3D12PipelineState meshletPso;
    // Transient skinned out-buffers, keyed by skinned renderer instance, recreated when its vertex count changes.
    // Pos/Normal are float3, Tangent float4 — same layout as the source streams so they're drop-in vertex buffers.
    // GPU-written (UAV) then GPU-read (vertex buffer) the SAME frame; under P0b overlap a frame N+1 reuse can't
    // race frame N's read because each skinned renderer's buffers are only rewritten by ITS dispatch each frame
    // and the draw that reads them is in the same command list after a UAV→vertex barrier (intra-frame ordered).
    public sealed class SkinnedBuffers {
        public ID3D12Resource Pos, Normal, Tangent; public int VertexCount;
        public ResourceStates State = ResourceStates.UnorderedAccess;   // tracked so barriers never mismatch (GBV)
    }
    readonly Dictionary<IStaticMeshRenderer, SkinnedBuffers> skinnedBuffers = new();
    int cullParamSlotSize;       // shadow CullParams slot
    int geoCullParamSlotSize;    // geometry GeoCullParams slot (bigger — has the Hi-Z fields)
    int metaStride, perDrawStride, materialStride;
    // P0b frame-overlap: every per-frame CPU-mapped UPLOAD buffer is N-buffered (×dev.FramesInFlight) so the CPU
    // writing frame F+1's slab can't stomp data the GPU is still reading from frame F. Each *FrameStride is the
    // byte span of ONE frame's region; the live slot is dev.FrameSlot*stride. At FramesInFlight==1 every stride
    // multiplies a single-frame alloc and FrameSlot is always 0 → offsets are 0 → byte-identical to pre-P0b.
    long metaFrameStride;          // metaUpload: metaStride*Capacity bytes per frame
    long cullParamFrameStride;     // cullParamUpload: geoCullParamSlotSize*MaxGroups bytes per frame
    long materialsFrameStride;     // materials: materialStride*MaxMaterials bytes per frame
    public long LastTris;        // triangles fed to the GPU cull this frame (pre-cull upper bound, for stats)
    public int LastSubmeshes;    // submeshes fed to the GPU cull this frame

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct SubmeshMeta {
        public Matrix4x4 Mvp; public Matrix4x4 Model;
        public Vector4 AabbMin; public Vector4 AabbMax;
        public uint FirstIndex, IndexCount, MaterialId, Flags;
        public uint LodCount; public float LodBias; public uint Lp0, Lp1;   // geometric LOD: count + per-submesh bias
        public uint LodR0a, LodR0b, LodR1a, LodR1b, LodR2a, LodR2b, LodR3a, LodR3b;  // LodRanges[4] (FirstIndex,IndexCount)
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
    // Geometry cull params (matches GpuCull.hlsl's bigger cbuffer — adds the Hi-Z fields).
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct GeoCullParams {
        public Vector4 P0, P1, P2, P3, P4, P5;
        public uint SubmeshCount, OutBase, HizEnabled, HizIndex;
        public Matrix4x4 ViewProj;   // unjittered
        public Matrix4x4 View;
        public Vector4 HizParams;    // x=w, y=h, z=mipCount, w=near
        public Vector4 HizFar;       // x=far
        public Vector4 LodSpanThresholds;   // x=LOD1 thr, y=LOD2, z=LOD3, w=LOD4 (px)
        public Vector4 LodControl;          // x=globalBias, y=lodEnabled(0/1), zw spare
    }

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
    long shadowMetaFrameStride;        // P0b: shadowMetaUpload bytes per frame (shadowMetaStride*ShadowCapacity)
    long shadowCullParamFrameStride;   // P0b: shadowCullParamUpload bytes per frame
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
        hiz = new Dx12HiZ(dev);
    }

    unsafe void BuildPipelines() {
        // --- Cull root sig (PLAIN, used by the SHADOW cull): CBV b0 + SRV t0 + UAV u0/u1 ---
        var cullParams = new[] {
            new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All),
            new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All),
            new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(0, 0), ShaderVisibility.All),
            new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(1, 0), ShaderVisibility.All),
        };
        cullRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, cullParams)));

        // --- Geometry cull root sig: same + a static point sampler (s0) + directly-indexed flag so the
        // cull can sample the Hi-Z pyramid via ResourceDescriptorHeap[HizIndex]. ---
        var pointClamp = new StaticSamplerDescription(ShaderVisibility.All, 0, 0) {
            Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        geoCullRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(
                RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
                cullParams, new[] { pointClamp })));

        // --- R3b compute-skinning root sig: CBV b0 (SkinParams) + root SRV t0 bones + t1..t5 in-streams +
        // root UAV u0..u2 out-streams. ALL root descriptors (raw buffers) — no descriptor heap, so the descriptor-
        // lifetime hang class (R2) can't apply here. ---
        var skinParams = new[] {
            new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All),
            new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All), // t0 bones
            new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.All), // t1 pos
            new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(2, 0), ShaderVisibility.All), // t2 normal
            new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(3, 0), ShaderVisibility.All), // t3 tangent
            new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(4, 0), ShaderVisibility.All), // t4 boneIdx
            new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(5, 0), ShaderVisibility.All), // t5 boneWt
            new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(0, 0), ShaderVisibility.All), // u0 outPos
            new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(1, 0), ShaderVisibility.All), // u1 outNormal
            new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(2, 0), ShaderVisibility.All), // u2 outTangent
        };
        skinRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, skinParams)));
        skinPso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription {
            RootSignature = skinRootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute,
                EmbeddedShaderSource.ReadHlsl("SkinCompute.hlsl"), "CSMain", "SkinCompute.hlsl"),
        });

        // --- R4 mesh-shader meshlet pipeline (AS+MS+PS). Root sig: root const b0 (DrawIndex/MeshletBase/Count) +
        // root SRV t0..t9 (PerDraws, GpuMaterials, Meshlets, Bounds, Verts, Prims, Pos, Normal, UV, Tangent) + CBV
        // b1 (motion) + CBV b2 (cull planes) + static sampler s0 + the directly-indexed flag (PS reads
        // ResourceDescriptorHeap for bindless material textures). Built only when HW mesh shaders are available. ---
        if (dev.HasMeshShaders) {
            var mp = new System.Collections.Generic.List<RootParameter1> {
                new(new RootConstants(0, 0, 4), ShaderVisibility.All),   // b0 DrawIndexCB (4 uints)
            };
            for (int t = 0; t <= 9; t++)   // t0..t9 root SRVs
                mp.Add(new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1((uint)t, 0), ShaderVisibility.All));
            mp.Add(new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.All)); // b1 motion
            mp.Add(new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(2, 0), ShaderVisibility.All)); // b2 cull
            var msWrap = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
                Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap,
                AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 16, ComparisonFunction = ComparisonFunction.Never,
                MinLOD = 0, MaxLOD = float.MaxValue,
            };
            // s1 point-clamp for the AS meshlet Hi-Z occlusion sample (visible to the amplification stage = All).
            var msPoint = new StaticSamplerDescription(ShaderVisibility.All, 1, 0) {
                Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1, ComparisonFunction = ComparisonFunction.Never,
                MinLOD = 0, MaxLOD = float.MaxValue,
            };
            meshletRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
                new RootSignatureDescription1(
                    RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
                    mp.ToArray(), new[] { msWrap, msPoint })));
            string mh = EmbeddedShaderSource.ReadHlsl("MeshletGBuffer.hlsl");
            byte[] asb = Dx12ShaderCompiler.Compile(DxcShaderStage.Amplification, mh, "ASMain", "MeshletGBuffer.hlsl");
            byte[] msb = Dx12ShaderCompiler.Compile(DxcShaderStage.Mesh, mh, "MSMain", "MeshletGBuffer.hlsl");
            byte[] psb = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, mh, "PSMain", "MeshletGBuffer.hlsl");
            meshletPso = Dx12MeshShaderPso.Create(dev.Device, meshletRootSig, asb, msb, psb,
                RasterizerDescription.CullClockwise, BlendDescription.Opaque, DepthStencilDescription.Default,
                Dx12GBuffer.ColorFormats, Dx12GBuffer.DepthFormat);
        }

        string cullHlsl = EmbeddedShaderSource.ReadHlsl("GpuCull.hlsl");
        cullPso = dev.Device.CreateComputePipelineState(new ComputePipelineStateDescription {
            RootSignature = geoCullRootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, cullHlsl, "CSMain", "GpuCull.hlsl"),
        });

        // --- Draw root sig: root const b0 (DrawIndex) + SRV t0 (PerDraws) + SRV t1 (GpuMaterials) +
        // CBV b1 (MotionConstants, per pass — matches the CPU GBuffer.hlsl b1) + bindless ---
        var drawParams = new[] {
            new RootParameter1(new RootConstants(0, 0, 1), ShaderVisibility.Vertex),
            new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.Vertex),
            new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.Pixel),
            new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.Pixel),
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

        metaFrameStride = (long)metaStride * Capacity;
        metaUpload = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(metaFrameStride * dev.FramesInFlight)), ResourceStates.GenericRead);
        metaMapped = metaUpload.Map<byte>(0);

        cullParamSlotSize = (System.Runtime.InteropServices.Marshal.SizeOf<CullParams>() + 255) & ~255;
        geoCullParamSlotSize = (System.Runtime.InteropServices.Marshal.SizeOf<GeoCullParams>() + 255) & ~255;
        cullParamFrameStride = (long)geoCullParamSlotSize * MaxGroups;
        cullParamUpload = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(cullParamFrameStride * dev.FramesInFlight)), ResourceStates.GenericRead);
        cullParamMapped = cullParamUpload.Map<byte>(0);

        materialsFrameStride = (long)materialStride * MaxMaterials;
        materials = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(materialsFrameStride * dev.FramesInFlight)), ResourceStates.GenericRead);
        materialsMapped = materials.Map<byte>(0);

        // R2: CPU per-submesh bindless PerDraws — UPLOAD heap (CPU-mapped), N-buffered by FrameSlot like materials.
        cpuPerDrawsFrameStride = (long)perDrawStride * MaxCpuDraws;
        cpuPerDraws = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(cpuPerDrawsFrameStride * dev.FramesInFlight)), ResourceStates.GenericRead);
        cpuPerDrawsMapped = cpuPerDraws.Map<byte>(0);
    }

    // Drop the cached material table so the NEXT EnsureMaterialTable rebuilds from scratch. Called on a scene
    // swap: the table is cached by a cheap count stamp (renderer + submesh count), so a new scene with the SAME
    // counts as the old one would keep the old scene's Material->id map and bindless texture binds — then
    // RenderInto's `materialIds.TryGetValue(mat)` misses every new-scene material and skips its submeshes (the
    // "second scene culls/renders wrong" bug). The dictionaries hold dead Material/Texture refs across the swap
    // anyway, so clear them too. -1 stamp forces a rebuild even if the new counts coincide.
    public void Invalidate() {
        tableStamp = -1;
        materialIds.Clear();
        bindlessIds.Clear();
        materialCount = 0;
        hizBindlessIndex = -1;
        // Drop the Hi-Z pyramid too: a same-resolution scene swap leaves it holding the OLD scene's depth, so
        // the occlusion cull would reject the new scene behind stale occluders. Next BuildHiZ re-creates it
        // (recreated=true → bindless SRV re-pointed) and refills from the new depth.
        hiz?.Invalidate();
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
        hizBindlessIndex = -1;   // the Hi-Z SRV lived in the bindless heap that was just reset — re-register

        foreach (var r in wholeMesh) {
            Mesh mesh = r.SharedMesh; if (mesh is null) continue;
            for (int s = 0; s < mesh.SubMeshes.Length; s++)
                RegisterMaterial(r.MaterialFor(s));
        }
    }

    // The bindless material table is sized for `MaxMaterials` GpuMaterial entries (AllocateBuffers).
    const int MaxMaterials = 4096;

    // Register one opaque material into the bindless table if it isn't there yet, writing its GpuMaterial
    // (byte-identical decode to GBufferBindless.hlsl). No-op for null / transparent / already-present / table-
    // full. Factored out of EnsureMaterialTable so the RT geometry build (Dx12RtGeometry) can resolve-or-
    // register the SAME ids — see ResolveOrRegisterMaterialId.
    unsafe void RegisterMaterial(Material mat) {
        if (mat is null || mat.Transparent || materialIds.ContainsKey(mat) || materialCount >= MaxMaterials) return;
        int id = materialCount++;
        materialIds[mat] = id;
        // Material-shaping fields read through the shader-declared property bag (semantic-keyed); the
        // map-presence flags (HasMetallicMap/HasRoughnessMap) are derived from the texture slots, not
        // authored properties. Byte-identical to the old typed-field reads (the bag mirrors them 1:1).
        bool hasMetal = mat.GetTexture(MaterialSemantic.MetallicMap) is not null;
        bool hasRough = mat.GetTexture(MaterialSemantic.RoughnessMap) is not null;
        var ec = mat.GetVector(MaterialSemantic.EmissiveColor);
        float ei = mat.GetFloat(MaterialSemantic.EmissiveIntensity);
        var gm = new GpuMaterial {
            DiffuseIdx = (uint)Bindless(mat.GetTexture(MaterialSemantic.DiffuseMap), TextureType.Diffuse),
            NormalIdx = (uint)Bindless(mat.GetTexture(MaterialSemantic.NormalMap), TextureType.Normal),
            MetallicIdx = (uint)Bindless(mat.GetTexture(MaterialSemantic.MetallicMap), TextureType.Metallic),
            RoughnessIdx = (uint)Bindless(mat.GetTexture(MaterialSemantic.RoughnessMap), TextureType.Roughness),
            AoIdx = (uint)Bindless(mat.GetTexture(MaterialSemantic.AOMap), TextureType.AO),
            EmissiveIdx = (uint)Bindless(mat.GetTexture(MaterialSemantic.EmissiveMap), TextureType.Emissive),
            BaseColorFactor = mat.GetVector(MaterialSemantic.BaseColorFactor),
            // W stays 0 (the old code multiplied a W=0 vector by intensity); the bag holds EmissiveColor
            // with W=1, so build the RGB-only vector explicitly to preserve the exact bytes.
            EmissiveFactor = new Vector4(ec.X, ec.Y, ec.Z, 0f) * ei,
            Metallic = mat.GetFloat(MaterialSemantic.MetallicFactor), Roughness = mat.GetFloat(MaterialSemantic.RoughnessFactor),
            SpecularReflectance = mat.GetFloat(MaterialSemantic.SpecularReflectance), NormalStrength = mat.GetFloat(MaterialSemantic.NormalStrength),
            NormalFlipY = mat.GetFloat(MaterialSemantic.NormalFlipY), HasMetallicMap = hasMetal ? 1f : 0f,
            HasRoughnessMap = hasRough ? 1f : 0f, PackedOrm = mat.GetFloat(MaterialSemantic.PackedOrm),
            Cutout = mat.GetFloat(MaterialSemantic.Cutout), HasEmissive = mat.GetFloat(MaterialSemantic.IsEmissive),
        };
        // materials is stamp-gated (rebuilt only on a material-set change, not per frame), but N-buffered for
        // overlap safety: when a rebuild lands on frame F, an in-flight earlier frame may still be reading its
        // own slab, and a later frame will read a DIFFERENT slot. The data is frame-invariant (same stamp = same
        // bytes), so write the entry into ALL N slabs (like ClusteredLights' clusterMin/Max) — every slot holds
        // correct material data regardless of which FrameSlot the next draw reads from.
        for (int f = 0; f < dev.FramesInFlight; f++)
            *(GpuMaterial*)(materialsMapped + f * materialsFrameStride + (long)id * materialStride) = gm;
    }

    // R1.0 (GI Pragmatic Revival) — robust per-submesh MaterialId for the RT geometry build. Dx12RtGeometry
    // bakes one MaterialId per triangle so the DXR hit shaders decode the SAME material the raster G-buffer
    // does. EnsureMaterialTable only registers WHOLE-MESH (SubMeshIndex<0) renderers, but the TLAS/rtGeometry
    // trace EVERY active renderer (incl. SubMeshIndex>=0 split-import children). Those renderers' materials
    // were absent from the table, so the old `TryMaterialId-or-0` fallback silently shaded their triangles
    // with material 0 (the first whole-mesh material) — RT-GI/emissive/reflection bounce came out wrong/empty
    // off color-only & split content while the raster G-buffer looked correct. This resolve-or-register makes
    // every RT-traced submesh's material present (extending the live table; the next EnsureMaterialTable reset
    // re-registers whole-mesh first, then rtGeometry's stamp-tracked rebuild re-adds these — ids stay
    // consistent). Returns -1 for null/transparent (the caller leaves such triangles at id 0; transparent
    // surfaces bounce negligibly and are skipped by the raster path too).
    public int ResolveOrRegisterMaterialId(Material mat) {
        if (mat is null || mat.Transparent) return -1;
        if (materialIds.TryGetValue(mat, out int id)) return id;
        RegisterMaterial(mat);
        return materialIds.TryGetValue(mat, out id) ? id : -1;   // -1 only if the table was full
    }

    // RT exposure: the DXR GI/reflection hit shaders decode the hit material BYTE-IDENTICALLY to the raster
    // G-buffer, so they reuse THIS exact bindless material table (no parallel build → no drift). The table is
    // a root SRV in the raster draw; here we hand the RT pass its GPU address + the Material→id map so it can
    // build a per-triangle MaterialId buffer (Dx12RtGeometry) that resolves the same ids GBufferBindless uses.
    // Live FrameSlot's material slab (all N slabs hold identical, frame-invariant data — see RegisterMaterial —
    // so the RT/Lumen/reflection passes that read this within the same frame decode the same bytes the raster
    // draw does). FramesInFlight==1 → FrameSlot 0 → offset 0 → byte-identical to pre-P0b.
    public ulong MaterialsGpuAddress => materials is null ? 0 : materials.GPUVirtualAddress + (ulong)(dev.FrameSlot * materialsFrameStride);
    public int MaterialCount => materialCount;

    // ===================== R2 — CPU per-submesh bindless draw =====================
    // The CPU per-submesh opaque loop reuses the GPU-driven bindless draw PSO/root sig (GBufferBindless.hlsl —
    // shading byte-identical to GBuffer.hlsl) instead of binding 6 material descriptors per draw. The CPU writes
    // each draw's PerDraw{Mvp,Model,MaterialId} into cpuPerDraws (its own N-buffered upload buffer) and selects it
    // with the DrawIndex root constant — exactly like the indirect path, just CPU-submitted one draw at a time.
    //
    // Bind ONCE before the CPU draws: root sig + PSO, PerDraws(t0)=cpuPerDraws (this frame's slot), GpuMaterials
    // (t1) = the shared table, Motion CBV (b1), and the bindless heap. The geometry pass already bound the bindless
    // heap via SetDescriptorHeaps(srvVisible.Heap)? — NO: bindless needs Dx12Backend.BindlessHeap. The caller binds
    // it here. The vertex/index buffers are still bound per-renderer by the caller (mesh streams differ per mesh).
    public void CpuBindlessBegin(ID3D12GraphicsCommandList4 cl, ulong motionCbAddress) {
        // ORDER IS LOAD-BEARING: SetDescriptorHeaps MUST precede SetGraphicsRootSignature for a root sig with the
        // CBV_SRV_UAV_HEAP_DIRECTLY_INDEXED flag (else GBV: DescriptorHeapNotSetBeforeRootSignature... → GPU hang).
        // The GPU-driven RenderInto does the same ("GOTCHA: before the root sig"). Getting this backwards was the
        // DRED PageFaultVA=0 "bad bind" hang on the forced-CPU Bistro stress.
        cl.SetDescriptorHeaps(Dx12Backend.BindlessHeap.Heap);   // ResourceDescriptorHeap[] source for the bindless reads
        cl.SetGraphicsRootSignature(drawRootSig);
        cl.SetPipelineState(drawPso);
        cl.SetGraphicsRootShaderResourceView(1,                  // t0 PerDraws (this frame's slot)
            cpuPerDraws.GPUVirtualAddress + (ulong)(dev.FrameSlot * cpuPerDrawsFrameStride));
        cl.SetGraphicsRootShaderResourceView(2, MaterialsGpuAddress);   // t1 GpuMaterials (this frame's slot)
        cl.SetGraphicsRootConstantBufferView(3, motionCbAddress);       // b1 MotionConstants (per pass)
    }

    // R3b — SkinParams CB (just VertexCount), N-buffered + a small per-dispatch stride so several skinned
    // renderers in one frame don't stomp each other's CB. Lazily created.
    ID3D12Resource skinCb; unsafe byte* skinCbMapped; int skinCbStride; long skinCbFrameStride;
    const int MaxSkinnedPerFrame = 256;
    unsafe void EnsureSkinCb() {
        if (skinCb != null) return;
        skinCbStride = 256;   // one SkinParams slot (CB alignment)
        skinCbFrameStride = (long)skinCbStride * MaxSkinnedPerFrame;
        skinCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(skinCbFrameStride * dev.FramesInFlight)), ResourceStates.GenericRead);
        skinCbMapped = skinCb.Map<byte>(0);
    }

    // Ensure this skinned renderer's transient out-buffers exist at the right vertex count (recreated on change).
    SkinnedBuffers EnsureSkinnedBuffers(IStaticMeshRenderer r, int vertexCount) {
        if (!skinnedBuffers.TryGetValue(r, out var sb)) { sb = new SkinnedBuffers(); skinnedBuffers[r] = sb; }
        if (sb.VertexCount == vertexCount && sb.Pos != null) return sb;
        // Vertex count changed (or first use) — (re)create. Defer-release the old ones under overlap.
        if (sb.Pos != null) { dev.DeferredRelease(sb.Pos); dev.DeferredRelease(sb.Normal); dev.DeferredRelease(sb.Tangent); }
        ID3D12Resource MakeUav(int bytes) => dev.Device.CreateCommittedResource(
            HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)bytes, ResourceFlags.AllowUnorderedAccess), ResourceStates.UnorderedAccess);
        sb.Pos = MakeUav(vertexCount * 12);      // float3
        sb.Normal = MakeUav(vertexCount * 12);   // float3
        sb.Tangent = MakeUav(vertexCount * 16);  // float4
        sb.VertexCount = vertexCount;
        return sb;
    }

    // R3b — dispatch compute skinning for one skinned renderer. `boneGpuAddr` = the transposed bone-matrix SRV
    // (the SAME buffer the skinned VS read). in* = the mesh's bind-pose streams (raw root SRVs). Writes the
    // animated pos/normal/tangent into this renderer's transient out-buffers, left in NonPixelShaderResource so
    // the subsequent draw can bind them as vertex buffers (a vertex buffer read is legal from a
    // VertexAndConstantBuffer state; the caller transitions out-buffers to that before the draw — see
    // SkinnedBuffersForDraw). Returns the out-buffers (or null if streams are missing). `skinIndex` = a per-frame
    // counter so each renderer's SkinParams CB slot is distinct.
    public unsafe SkinnedBuffers DispatchSkin(ID3D12GraphicsCommandList4 cl, IStaticMeshRenderer r, int skinIndex,
        ulong boneGpuAddr, ulong inPos, ulong inNormal, ulong inTangent, ulong inBoneIdx, ulong inBoneWt,
        int vertexCount) {
        if (vertexCount <= 0 || skinIndex >= MaxSkinnedPerFrame) return null;
        EnsureSkinCb();
        var sb = EnsureSkinnedBuffers(r, vertexCount);
        // SkinParams CB (VertexCount).
        long cbOff = (long)dev.FrameSlot * skinCbFrameStride + (long)skinIndex * skinCbStride;
        *(uint*)(skinCbMapped + cbOff) = (uint)vertexCount;
        // The out-buffers are in UnorderedAccess (fresh) or NonPixelShaderResource (from last frame's draw) —
        // ensure UAV for the dispatch write. Use the TRACKED state so the barrier's before-state always matches
        // (a fixed before-state mismatches on the first frame → GBV ResourceBarrierBeforeAfterMismatch).
        if (sb.State != ResourceStates.UnorderedAccess) {
            Barrier(cl, sb.Pos, sb.State, ResourceStates.UnorderedAccess);
            Barrier(cl, sb.Normal, sb.State, ResourceStates.UnorderedAccess);
            Barrier(cl, sb.Tangent, sb.State, ResourceStates.UnorderedAccess);
            sb.State = ResourceStates.UnorderedAccess;
        }
        cl.SetComputeRootSignature(skinRootSig);
        cl.SetPipelineState(skinPso);
        cl.SetComputeRootConstantBufferView(0, skinCb.GPUVirtualAddress + (ulong)cbOff);
        cl.SetComputeRootShaderResourceView(1, boneGpuAddr);
        cl.SetComputeRootShaderResourceView(2, inPos);
        cl.SetComputeRootShaderResourceView(3, inNormal);
        cl.SetComputeRootShaderResourceView(4, inTangent);
        cl.SetComputeRootShaderResourceView(5, inBoneIdx);
        cl.SetComputeRootShaderResourceView(6, inBoneWt);
        cl.SetComputeRootUnorderedAccessView(7, sb.Pos.GPUVirtualAddress);
        cl.SetComputeRootUnorderedAccessView(8, sb.Normal.GPUVirtualAddress);
        cl.SetComputeRootUnorderedAccessView(9, sb.Tangent.GPUVirtualAddress);
        cl.Dispatch((uint)((vertexCount + 63) / 64), 1, 1);
        // UAV write done → transition out-buffers to a state legal as a vertex-buffer input for the draw.
        // VertexAndConstantBuffer is the correct read state for IASetVertexBuffers.
        Barrier(cl, sb.Pos, ResourceStates.UnorderedAccess, ResourceStates.VertexAndConstantBuffer);
        Barrier(cl, sb.Normal, ResourceStates.UnorderedAccess, ResourceStates.VertexAndConstantBuffer);
        Barrier(cl, sb.Tangent, ResourceStates.UnorderedAccess, ResourceStates.VertexAndConstantBuffer);
        sb.State = ResourceStates.VertexAndConstantBuffer;
        return sb;
    }

    static void Barrier(ID3D12GraphicsCommandList4 cl, ID3D12Resource res, ResourceStates from, ResourceStates to) {
        if (from != to) cl.ResourceBarrierTransition(res, from, to);
    }

    // Write one CPU draw's PerDraw entry into this frame's slot at `drawIndex` and return whether it fit
    // (drawIndex < MaxCpuDraws). The caller then SetGraphicsRoot32BitConstant(0, drawIndex) + DrawIndexedInstanced.
    public unsafe bool CpuBindlessWrite(int drawIndex, Matrix4x4 mvp, Matrix4x4 model, int materialId) {
        if ((uint)drawIndex >= MaxCpuDraws) return false;
        long off = (long)dev.FrameSlot * cpuPerDrawsFrameStride + (long)drawIndex * perDrawStride;
        *(PerDraw*)(cpuPerDrawsMapped + off) = new PerDraw {
            Mvp = Matrix4x4.Transpose(mvp), Model = Matrix4x4.Transpose(model), MaterialId = (uint)materialId,
        };
        return true;
    }
    public bool TryMaterialId(Material mat, out int id) {
        if (mat is not null) return materialIds.TryGetValue(mat, out id);
        id = 0; return false;
    }
    // The material-table stamp — Dx12RtGeometry rebuilds its per-triangle buffer when this changes (a new
    // material set means the ids it baked are stale).
    public int MaterialTableStamp => tableStamp;

    // Resolve a material map to a bindless index (real texture or the neutral fallback — matches CPU BindSrv).
    int Bindless(Texture2D tex, TextureType type) {
        var dx = (tex as Dx12Texture2D) ?? (DefaultTextures.Neutral(type) as Dx12Texture2D);
        if (dx is null) return 0;
        if (bindlessIds.TryGetValue(dx, out int idx)) return idx;
        idx = Dx12Backend.RegisterBindless(dx.SrvCpu);
        bindlessIds[dx] = idx;
        return idx;
    }

    // Build the Hi-Z pyramid from the previous frame's G-buffer depth (must already be NonPixelShaderResource).
    // `enabled` = the camera-delta gate (false 1 frame after a big jump, and on the first frame). Own ExecuteSync.
    public void BuildHiZ(CpuDescriptorHandle depthSrvCpu, int w, int h, bool enabled) {
        hizOnThisFrame = enabled;
        if (!enabled) return;
        // EF3 RESIZE HANG FIX: Ensure() returns true when it RECREATED the pyramid (resize → new dimensions →
        // new resource + new mip count). The cull shader reads the pyramid through the BINDLESS heap slot
        // hizBindlessIndex (ResourceDescriptorHeap[index]); that descriptor must be re-pointed at the NEW
        // resource, or the cull samples the DISPOSED old pyramid → GPU device-removal/hang (DXGI_DEVICE_HUNG,
        // PageFaultVA=0). Previously CreateAllMipsSrv ran ONLY on first build (hizBindlessIndex < 0), so after
        // any resize the bindless descriptor was stale — the editor's resize crash. Re-register whenever the
        // pyramid was (re)created OR the slot is unallocated.
        bool recreated = hiz.Ensure(w, h);   // true on FIRST build (pyramid was null) AND on every resize
        // The bindless slot must be (re-)written when it was freshly allocated, NOT only when the pyramid was
        // recreated. EnsureMaterialTable calls BindlessHeap.Reset() on a material-table rebuild and sets
        // hizBindlessIndex = -1 (the old slot is gone). On a SCENE SWAP the resolution is unchanged, so
        // hiz.Ensure() returns false (no recreation) — but the slot below is freshly Allocate()d into a heap
        // whose descriptor was never written. Re-pointing only on `recreated` left that new slot pointing at
        // garbage → the cull sampled junk Hi-Z depth → whole new scene wrongly occlusion-culled (the "culling
        // breaks after switching scenes" bug). Re-write whenever the slot was just allocated OR the pyramid was
        // recreated. (Same class of bug as the EF3 resize hang: a cached bindless descriptor not re-pointed
        // after its backing heap/resource changed.)
        bool slotReallocated = hizBindlessIndex < 0;
        if (slotReallocated)
            hizBindlessIndex = Dx12Backend.BindlessHeap.Allocate();
        if (recreated || slotReallocated)
            hiz.CreateAllMipsSrv(Dx12Backend.BindlessHeap.Cpu(hizBindlessIndex));
        dev.ExecuteSync(cl => hiz.Build(cl, depthSrvCpu));
    }

    // Record the cull + ExecuteIndirect for all whole-mesh groups into the geometry command list (which has
    // the G-buffer MRT + viewport already bound). `frustumPlanes` are the SAME 6 normalized planes the CPU
    // cull uses (from viewProjUnjittered). viewProjUnjittered/view drive the Hi-Z occlusion test. Returns the
    // ExecuteIndirect count for stats.
    // R4 — cull-planes CB for the meshlet AS shader (b2), N-buffered. Lazily created.
    ID3D12Resource meshletCullCb; unsafe byte* meshletCullCbMapped; long meshletCullCbStride;
    unsafe void EnsureMeshletCullCb() {
        if (meshletCullCb != null) return;
        meshletCullCbStride = 512;   // Planes[6](96)+CameraPos(16)+HizVP(64)+HizView(64)+HizParams(16)+HizFar(16)=272
        meshletCullCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(meshletCullCbStride * dev.FramesInFlight)), ResourceStates.GenericRead);
        meshletCullCbMapped = meshletCullCb.Map<byte>(0);
    }

    public bool MeshletAvailable => meshletPso != null;
    public long MeshletTris => meshletTris;
    long meshletTris;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct MeshletDrawConst { public uint DrawIndex, MeshletBase, MeshletCount, Pad; }

    // R4 — draw `renderers` through the MESH-SHADER meshlet pipeline (one DispatchMesh per submesh; the AS shader
    // frustum/sphere-culls meshlets). Reuses the bindless material table + cpuPerDraws (PerDraw{Mvp,Model,MatId}).
    // Shading is byte-identical to GBufferBindless (MeshletGBuffer.PSMain is a verbatim copy). Returns draw count.
    // R5 — exposes the pieces the visibility-buffer pass needs to reuse the GPU-driven substrate.
    public ulong CpuPerDrawsAddress => cpuPerDraws.GPUVirtualAddress + (ulong)(dev.FrameSlot * cpuPerDrawsFrameStride);
    public ulong MeshletCullCbAddress { get { EnsureMeshletCullCb(); return meshletCullCb.GPUVirtualAddress + (ulong)(dev.FrameSlot * meshletCullCbStride); } }

    // R5 — raster the visibility id with `visPso`/`visRootSig` (VisBuffer.hlsl). Same meshlet draw loop as
    // RenderIntoMeshlet but binds only PerDraws(t0)+meshlet SRVs(t2-t6)+cull CB(b2); the vis PS writes only the id.
    // Fills the meshlet cull CB (frustum+cone+Hi-Z) exactly like RenderIntoMeshlet. Returns the draw count.
    public unsafe int RenderVis(ID3D12GraphicsCommandList6 cl, List<IStaticMeshRenderer> renderers,
        ID3D12RootSignature visRootSig, ID3D12PipelineState visPso,
        Matrix4x4 viewProj, Vector4[] frustumPlanes, Vector3 cameraPos, bool coneCull,
        Matrix4x4 viewProjUnjittered, Matrix4x4 view, float near, float far, ref int cpuDrawIndex) {
        if (renderers.Count == 0) return 0;
        EnsureMeshletCullCb();
        long cullOff = (long)dev.FrameSlot * meshletCullCbStride;
        byte* cb = meshletCullCbMapped + cullOff;
        var pd = (Vector4*)cb;
        for (int i = 0; i < 6; i++) pd[i] = frustumPlanes[i];
        pd[6] = new Vector4(cameraPos, coneCull ? 1f : 0f);
        long oo = 7 * 16;
        *(Matrix4x4*)(cb + oo) = Matrix4x4.Transpose(viewProjUnjittered); oo += 64;
        *(Matrix4x4*)(cb + oo) = Matrix4x4.Transpose(view); oo += 64;
        bool hizOn = hizOnThisFrame && hizBindlessIndex >= 0;
        *(Vector4*)(cb + oo) = new Vector4(hiz.Width, hiz.Height, hiz.MipCount, near); oo += 16;
        *(Vector4*)(cb + oo) = new Vector4(far, hizOn ? 1f : 0f, Math.Max(hizBindlessIndex, 0), 0f);

        cl.SetDescriptorHeaps(Dx12Backend.BindlessHeap.Heap);
        cl.SetGraphicsRootSignature(visRootSig);
        cl.SetPipelineState(visPso);
        cl.SetGraphicsRootShaderResourceView(1, CpuPerDrawsAddress);   // t0 PerDraws
        cl.SetGraphicsRootConstantBufferView(12, meshletCullCb.GPUVirtualAddress + (ulong)cullOff); // b2 cull
        int draws = 0;
        foreach (var r in renderers) {
            Mesh mesh = r.SharedMesh; if (mesh is null) continue;
            var pos = mesh.VertexBuffer as Dx12Buffer<GLVector3>;
            if (pos?.Resource is null) continue;
            Matrix4x4 model = ToNum(r.Transform.WorldMatrix);
            Matrix4x4 mvp = model * viewProj;
            int only = r.SubMeshIndex;
            int sFirst = only >= 0 ? only : 0;
            int sLast = only >= 0 ? only : mesh.SubMeshes.Length - 1;
            cl.SetGraphicsRootShaderResourceView(7, pos.GpuAddress);   // t6 Positions
            for (int s = sFirst; s <= sLast; s++) {
                if ((uint)s >= (uint)mesh.SubMeshes.Length) break;
                SubMeshData sub = mesh.SubMeshes[s];
                if (sub.IndexCount <= 0) continue;
                Material mat = r.MaterialFor(s);
                if (mat is null || mat.Transparent) continue;
                int mid = materialIds.TryGetValue(mat, out int rid) ? rid : ResolveOrRegisterMaterialId(mat);
                if (mid < 0) continue;
                if ((uint)cpuDrawIndex >= MaxCpuDraws) break;
                long pdo = (long)dev.FrameSlot * cpuPerDrawsFrameStride + (long)cpuDrawIndex * perDrawStride;
                *(PerDraw*)(cpuPerDrawsMapped + pdo) = new PerDraw {
                    Mvp = Matrix4x4.Transpose(mvp), Model = Matrix4x4.Transpose(model), MaterialId = (uint)mid,
                };
                var ml = Dx12Meshlet.Build(dev, mesh, s);
                cl.SetGraphicsRootShaderResourceView(3, ml.Meshlets.GPUVirtualAddress);
                cl.SetGraphicsRootShaderResourceView(4, ml.Bounds.GPUVirtualAddress);
                cl.SetGraphicsRootShaderResourceView(5, ml.Verts.GPUVirtualAddress);
                cl.SetGraphicsRootShaderResourceView(6, ml.Prims.GPUVirtualAddress);
                var rc = new MeshletDrawConst { DrawIndex = (uint)cpuDrawIndex, MeshletBase = 0, MeshletCount = (uint)ml.MeshletCount, Pad = 0 };
                cl.SetGraphicsRoot32BitConstants(0, rc, 0);
                cl.DispatchMesh((uint)((ml.MeshletCount + 31) / 32), 1, 1);
                cpuDrawIndex++; draws++;
            }
        }
        return draws;
    }

    public unsafe int RenderIntoMeshlet(ID3D12GraphicsCommandList6 cl, List<IStaticMeshRenderer> renderers,
        Matrix4x4 viewProj, Vector4[] frustumPlanes, Vector3 cameraPos, bool coneCull,
        Matrix4x4 viewProjUnjittered, Matrix4x4 view, float near, float far,
        ulong motionCbAddress, ref int cpuDrawIndex) {
        if (meshletPso == null || renderers.Count == 0) return 0;
        meshletTris = 0;
        EnsureMeshletCullCb();
        // Cull CB (b2): 6 unjittered frustum planes + camera pos (xyz)/cone flag (w) + Hi-Z occlusion fields.
        long cullOff = (long)dev.FrameSlot * meshletCullCbStride;
        byte* cb = meshletCullCbMapped + cullOff;
        var planesDst = (Vector4*)cb;
        for (int i = 0; i < 6; i++) planesDst[i] = frustumPlanes[i];
        planesDst[6] = new Vector4(cameraPos, coneCull ? 1f : 0f);
        // Hi-Z: reuse the GPU-driven pyramid (same one ExecuteIndirect occludes against). Enabled only when the
        // pyramid is primed this frame + the bindless slot is valid. Conservative (never false-culls) → byte-id.
        long o = 7 * 16;   // after Planes[6] + CameraPosCull
        *(Matrix4x4*)(cb + o) = Matrix4x4.Transpose(viewProjUnjittered); o += 64;
        *(Matrix4x4*)(cb + o) = Matrix4x4.Transpose(view); o += 64;
        bool hizOn = hizOnThisFrame && hizBindlessIndex >= 0;
        *(Vector4*)(cb + o) = new Vector4(hiz.Width, hiz.Height, hiz.MipCount, near); o += 16;
        *(Vector4*)(cb + o) = new Vector4(far, hizOn ? 1f : 0f, Math.Max(hizBindlessIndex, 0), 0f);

        cl.SetDescriptorHeaps(Dx12Backend.BindlessHeap.Heap);   // ResourceDescriptorHeap[] for bindless materials
        cl.SetGraphicsRootSignature(meshletRootSig);
        cl.SetPipelineState(meshletPso);
        // Root params: 0=root const b0, 1..10=SRV t0..t9, 11=CBV b1 motion, 12=CBV b2 cull.
        cl.SetGraphicsRootShaderResourceView(1, cpuPerDraws.GPUVirtualAddress + (ulong)(dev.FrameSlot * cpuPerDrawsFrameStride)); // t0 PerDraws
        cl.SetGraphicsRootShaderResourceView(2, MaterialsGpuAddress);   // t1 GpuMaterials
        cl.SetGraphicsRootConstantBufferView(11, motionCbAddress);      // b1 motion
        cl.SetGraphicsRootConstantBufferView(12, meshletCullCb.GPUVirtualAddress + (ulong)cullOff); // b2 cull planes

        int draws = 0;
        foreach (var r in renderers) {
            Mesh mesh = r.SharedMesh; if (mesh is null) continue;
            var pos = mesh.VertexBuffer as Dx12Buffer<GLVector3>;
            var nrm = mesh.NormalBuffer as Dx12Buffer<GLVector3>;
            var uv = mesh.UvBuffer as Dx12Buffer<Vector2>;
            var tan = mesh.TangentBuffer as Dx12Buffer<Vector4>;
            if (pos?.Resource is null || nrm?.Resource is null || uv?.Resource is null || tan?.Resource is null) continue;
            Matrix4x4 model = ToNum(r.Transform.WorldMatrix);
            Matrix4x4 mvp = model * viewProj;
            int only = r.SubMeshIndex;
            int sFirst = only >= 0 ? only : 0;
            int sLast = only >= 0 ? only : mesh.SubMeshes.Length - 1;
            // Vertex streams bound as root SRVs (t6..t9) once per renderer (same mesh for all its submeshes).
            cl.SetGraphicsRootShaderResourceView(7, pos.GpuAddress);
            cl.SetGraphicsRootShaderResourceView(8, nrm.GpuAddress);
            cl.SetGraphicsRootShaderResourceView(9, uv.GpuAddress);
            cl.SetGraphicsRootShaderResourceView(10, tan.GpuAddress);
            for (int s = sFirst; s <= sLast; s++) {
                if ((uint)s >= (uint)mesh.SubMeshes.Length) break;
                SubMeshData sub = mesh.SubMeshes[s];
                if (sub.IndexCount <= 0) continue;
                Material mat = r.MaterialFor(s);
                if (mat is null || mat.Transparent) continue;
                int mid = materialIds.TryGetValue(mat, out int rid) ? rid : ResolveOrRegisterMaterialId(mat);
                if (mid < 0) continue;
                if ((uint)cpuDrawIndex >= MaxCpuDraws) break;
                // PerDraw entry (shared cpuPerDraws buffer).
                long pdOff = (long)dev.FrameSlot * cpuPerDrawsFrameStride + (long)cpuDrawIndex * perDrawStride;
                *(PerDraw*)(cpuPerDrawsMapped + pdOff) = new PerDraw {
                    Mvp = Matrix4x4.Transpose(mvp), Model = Matrix4x4.Transpose(model), MaterialId = (uint)mid,
                };
                var ml = Dx12Meshlet.Build(dev, mesh, s);
                // Per-submesh meshlet SRVs (t2..t5).
                cl.SetGraphicsRootShaderResourceView(3, ml.Meshlets.GPUVirtualAddress);
                cl.SetGraphicsRootShaderResourceView(4, ml.Bounds.GPUVirtualAddress);
                cl.SetGraphicsRootShaderResourceView(5, ml.Verts.GPUVirtualAddress);
                cl.SetGraphicsRootShaderResourceView(6, ml.Prims.GPUVirtualAddress);
                // DrawIndexCB b0: { DrawIndex, MeshletBase=0, MeshletCount, pad } — meshlets are per-submesh so base 0.
                var rc = new MeshletDrawConst { DrawIndex = (uint)cpuDrawIndex, MeshletBase = 0, MeshletCount = (uint)ml.MeshletCount, Pad = 0 };
                cl.SetGraphicsRoot32BitConstants(0, rc, 0);
                // One AS threadgroup per 32 meshlets.
                cl.DispatchMesh((uint)((ml.MeshletCount + 31) / 32), 1, 1);
                cpuDrawIndex++; draws++;
                meshletTris += sub.IndexCount / 3;   // pre-cull upper bound (for stats parity with ExecuteIndirect)
            }
        }
        return draws;
    }

    public unsafe int RenderInto(ID3D12GraphicsCommandList4 cl, List<IStaticMeshRenderer> wholeMesh,
                                 Matrix4x4 viewProj, Vector4[] frustumPlanes,
                                 Matrix4x4 viewProjUnjittered, Matrix4x4 view, float near, float far,
                                 ulong motionCbAddress) {
        // Group by mesh; build the flat SubmeshMeta array (per frame: Mvp depends on the camera).
        var groups = new List<(Dx12Buffer<GLVector3> vb, Dx12Buffer<GLVector3> nb,
            Dx12Buffer<Vector2> ub, Dx12Buffer<Vector4> tb,
            Dx12IndexBuffer ib, int baseIdx, int count)>();
        // DETERMINISTIC group order: a plain Dictionary's enumeration order is NOT stable (hash + insertion
        // history), so iterating it assigned SubmeshMeta slots — and thus the ExecuteIndirect DRAW ORDER — in a
        // run-to-run-varying order. For meshes that don't overlap (Bistro) draw order doesn't change pixels, so
        // it went unnoticed; but for OVERLAPPING coplanar geometry (split-import siblings at the same transform)
        // the last-writer at z-equal seams flipped, making the output non-deterministic AND diverge from the CPU
        // loop. Fix: order groups by each mesh's FIRST appearance in `wholeMesh` (the renderer iteration order the
        // CPU per-submesh loop also uses), via a parallel key list — so the draw order matches the CPU path.
        var meshOrder = new List<Mesh>();
        var byMesh = new Dictionary<Mesh, List<IStaticMeshRenderer>>();
        foreach (var r in wholeMesh) {
            Mesh m = r.SharedMesh; if (m is null) continue;
            if (!byMesh.TryGetValue(m, out var list)) { list = new(); byMesh[m] = list; meshOrder.Add(m); }
            list.Add(r);
        }

        // P0b: write into THIS frame's slab of the N-buffered uploads (offset 0 at FramesInFlight==1).
        long metaSlot = (long)dev.FrameSlot * metaFrameStride;
        long cullParamSlot = (long)dev.FrameSlot * cullParamFrameStride;

        int total = 0, groupCount = 0;
        long triAccum = 0;
        foreach (Mesh mesh in meshOrder) {
            if (groupCount >= MaxGroups) break;
            var kvValue = byMesh[mesh];
            var vb = mesh.VertexBuffer as Dx12Buffer<GLVector3>;
            var ib = mesh.IndexBuffer as Dx12IndexBuffer;
            var nb = mesh.NormalBuffer as Dx12Buffer<GLVector3>;
            var ub = mesh.UvBuffer as Dx12Buffer<Vector2>;
            var tb = mesh.TangentBuffer as Dx12Buffer<Vector4>;
            if (vb?.Resource is null || ib?.Resource is null || nb?.Resource is null ||
                ub?.Resource is null || tb?.Resource is null) continue;

            int groupBase = total;
            foreach (var r in kvValue) {
                Matrix4x4 model = ToNum(r.Transform.WorldMatrix);
                Matrix4x4 mvp = model * viewProj;
                // R3a: a split-import renderer (SubMeshIndex >= 0) draws ONLY its one submesh; a whole-mesh
                // renderer (< 0) draws all submeshes. Clamp the loop range per renderer so split-import children
                // can share the GPU-driven path without each one pulling in the whole mesh's submeshes.
                int only = r.SubMeshIndex;
                int sFirst = only >= 0 ? only : 0;
                int sLast = only >= 0 ? only : mesh.SubMeshes.Length - 1;
                for (int s = sFirst; s <= sLast && total < Capacity; s++) {
                    if ((uint)s >= (uint)mesh.SubMeshes.Length) break;
                    SubMeshData sub = mesh.SubMeshes[s];
                    if (sub.IndexCount <= 0) continue;
                    Material mat = r.MaterialFor(s);
                    if (mat is null || mat.Transparent) continue;   // transparents -> forward pass
                    if (!materialIds.TryGetValue(mat, out int matId)) continue;
                    mesh.GetSubMeshBounds(s, out GLVector3 lmin, out GLVector3 lmax);
                    WorldAabb(lmin, lmax, model, out Vector3 wlo, out Vector3 whi);
                    var meta = new SubmeshMeta {
                        Mvp = Matrix4x4.Transpose(mvp), Model = Matrix4x4.Transpose(model),
                        AabbMin = new Vector4(wlo, 0), AabbMax = new Vector4(whi, 0),
                        FirstIndex = (uint)sub.IndexStart, IndexCount = (uint)sub.IndexCount,
                        MaterialId = (uint)matId, Flags = 0,
                        LodCount = 1, LodBias = r.LodBias,
                    };
                    // Pack up to 4 extra LOD ranges (LOD1..4) so the GPU cull can pick by screen span. LodCount<=1
                    // ⇒ the shader takes the LOD0 (FirstIndex/IndexCount) branch → byte-identical when no chain.
                    if (sub.Lods is { Length: > 1 } lods) {
                        int n = Math.Min(lods.Length, 5);   // LOD0 + up to 4 extra
                        meta.LodCount = (uint)n;
                        if (n > 1) { meta.LodR0a = (uint)lods[1].FirstIndex; meta.LodR0b = (uint)lods[1].IndexCount; }
                        if (n > 2) { meta.LodR1a = (uint)lods[2].FirstIndex; meta.LodR1b = (uint)lods[2].IndexCount; }
                        if (n > 3) { meta.LodR2a = (uint)lods[3].FirstIndex; meta.LodR2b = (uint)lods[3].IndexCount; }
                        if (n > 4) { meta.LodR3a = (uint)lods[4].FirstIndex; meta.LodR3b = (uint)lods[4].IndexCount; }
                    }
                    *(SubmeshMeta*)(metaMapped + metaSlot + (long)total * metaStride) = meta;
                    triAccum += sub.IndexCount / 3;
                    total++;
                }
            }
            int groupTotal = total - groupBase;
            if (groupTotal == 0) continue;
            // Fill this group's GeoCullParams slot (planes + Hi-Z fields).
            var cp = new GeoCullParams {
                P0 = frustumPlanes[0], P1 = frustumPlanes[1], P2 = frustumPlanes[2],
                P3 = frustumPlanes[3], P4 = frustumPlanes[4], P5 = frustumPlanes[5],
                SubmeshCount = (uint)groupTotal, OutBase = (uint)groupBase,
                HizEnabled = hizOnThisFrame ? 1u : 0u, HizIndex = (uint)Math.Max(hizBindlessIndex, 0),
                ViewProj = Matrix4x4.Transpose(viewProjUnjittered), View = Matrix4x4.Transpose(view),
                HizParams = new Vector4(hiz.Width, hiz.Height, hiz.MipCount, near), HizFar = new Vector4(far, 0, 0, 0),
                LodSpanThresholds = new Vector4(LodSettings.SpanThresholds[0], LodSettings.SpanThresholds[1],
                                                LodSettings.SpanThresholds[2], LodSettings.SpanThresholds[3]),
                LodControl = new Vector4(LodSettings.GlobalBias, LodSettings.Active ? 1f : 0f, LodSettings.ForceLod, 0),
            };
            *(GeoCullParams*)(cullParamMapped + cullParamSlot + (long)groupCount * geoCullParamSlotSize) = cp;
            groups.Add((vb, nb, ub, tb, ib, groupBase, groupTotal));
            groupCount++;
        }
        if (groups.Count == 0) return 0;

        // 1) Transition outputs back to UAV (from last frame's draw states).
        cl.ResourceBarrierTransition(commands, ResourceStates.IndirectArgument, ResourceStates.UnorderedAccess);
        cl.ResourceBarrierTransition(perDraws, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);

        // 2) Cull dispatch per group (writes Commands + PerDraws; one slot per submesh, in order). Bind the
        // bindless heap FIRST (the cull samples the Hi-Z pyramid via ResourceDescriptorHeap; gotcha: before
        // the root sig). The same heap stays bound for the bindless GBuffer draw below.
        cl.SetDescriptorHeaps(Dx12Backend.BindlessHeap.Heap);
        cl.SetComputeRootSignature(geoCullRootSig);
        cl.SetPipelineState(cullPso);
        cl.SetComputeRootShaderResourceView(1, metaUpload.GPUVirtualAddress + (ulong)metaSlot);
        cl.SetComputeRootUnorderedAccessView(2, commands.GPUVirtualAddress);
        cl.SetComputeRootUnorderedAccessView(3, perDraws.GPUVirtualAddress);
        for (int g = 0; g < groups.Count; g++) {
            cl.SetComputeRootConstantBufferView(0, cullParamUpload.GPUVirtualAddress + (ulong)(cullParamSlot + (long)g * geoCullParamSlotSize));
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
        cl.SetGraphicsRootShaderResourceView(2, materials.GPUVirtualAddress + (ulong)(dev.FrameSlot * materialsFrameStride));
        cl.SetGraphicsRootConstantBufferView(3, motionCbAddress);   // b1 motion (per pass)
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
        shadowMetaFrameStride = (long)shadowMetaStride * ShadowCapacity;
        shadowMetaUpload = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(shadowMetaFrameStride * dev.FramesInFlight)), ResourceStates.GenericRead);
        shadowMetaMapped = shadowMetaUpload.Map<byte>(0);
        shadowCullParamFrameStride = (long)cullParamSlotSize * ShadowCascades * MaxGroups;
        shadowCullParamUpload = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(shadowCullParamFrameStride * dev.FramesInFlight)), ResourceStates.GenericRead);
        shadowCullParamMapped = shadowCullParamUpload.Map<byte>(0);
    }

    // Build the per-cascade SubmeshMeta + dispatch the shadow culls (records into `cl`). Must run BEFORE the
    // per-cascade depth draws. Mirrors the CPU shadow caster set EXACTLY (every submesh with IndexCount > 0,
    // no material filter — depth-only) so the shadow maps are byte-identical. Stores the slices for
    // DrawShadowCascade. cascadeMatrices = the 4 light-space view*proj matrices.
    // activeCascades = how many cascades the volume selected (1..ShadowCascades); culls/draws only those.
    public unsafe void BuildShadowCull(ID3D12GraphicsCommandList4 cl, List<IStaticMeshRenderer> wholeMesh,
                                       Matrix4x4[] cascadeMatrices, int activeCascades = ShadowCascades) {
        shadowSlices.Clear();
        int cascades = Math.Clamp(activeCascades, 1, ShadowCascades);
        // Deterministic group order (see RenderInto): Dictionary enumeration order is unstable. Shadow depth is
        // Less-tested so order rarely flips pixels, but keep it stable for parity with the geometry pass.
        var meshOrder = new List<Mesh>();
        var byMesh = new Dictionary<Mesh, List<IStaticMeshRenderer>>();
        foreach (var r in wholeMesh) {
            Mesh m = r.SharedMesh; if (m is null) continue;
            if (!byMesh.TryGetValue(m, out var list)) { list = new(); byMesh[m] = list; meshOrder.Add(m); }
            list.Add(r);
        }

        // P0b: write into THIS frame's slab (offset 0 at FramesInFlight==1).
        long shadowMetaSlot = (long)dev.FrameSlot * shadowMetaFrameStride;
        long shadowCullParamSlot = (long)dev.FrameSlot * shadowCullParamFrameStride;

        int total = 0, sliceCount = 0;
        var planes = new Vector4[6];
        for (int c = 0; c < cascades; c++) {
            ExtractPlanes(cascadeMatrices[c], planes);
            foreach (Mesh mesh in meshOrder) {
                if (sliceCount >= ShadowCascades * MaxGroups) break;
                var vb = mesh.VertexBuffer as Dx12Buffer<GLVector3>;
                var ib = mesh.IndexBuffer as Dx12IndexBuffer;
                if (vb?.Resource is null || ib?.Resource is null) continue;
                int sliceBase = total;
                foreach (var r in byMesh[mesh]) {
                    Matrix4x4 model = ToNum(r.Transform.WorldMatrix);
                    Matrix4x4 lightMvp = Matrix4x4.Transpose(model * cascadeMatrices[c]);
                    for (int s = 0; s < mesh.SubMeshes.Length && total < ShadowCapacity; s++) {
                        SubMeshData sub = mesh.SubMeshes[s];
                        if (sub.IndexCount <= 0) continue;
                        mesh.GetSubMeshBounds(s, out GLVector3 lmin, out GLVector3 lmax);
                        WorldAabb(lmin, lmax, model, out Vector3 wlo, out Vector3 whi);
                        *(ShadowMeta*)(shadowMetaMapped + shadowMetaSlot + (long)total * shadowMetaStride) = new ShadowMeta {
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
                *(CullParams*)(shadowCullParamMapped + shadowCullParamSlot + (long)sliceCount * cullParamSlotSize) = cp;
                shadowSlices.Add((c, vb, ib, sliceBase, count));
                sliceCount++;
            }
        }
        if (shadowSlices.Count == 0) return;

        cl.ResourceBarrierTransition(shadowCommands, ResourceStates.IndirectArgument, ResourceStates.UnorderedAccess);
        cl.ResourceBarrierTransition(shadowPerDraws, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);
        cl.SetComputeRootSignature(cullRootSig);
        cl.SetPipelineState(shadowCullPso);
        cl.SetComputeRootShaderResourceView(1, shadowMetaUpload.GPUVirtualAddress + (ulong)shadowMetaSlot);
        cl.SetComputeRootUnorderedAccessView(2, shadowCommands.GPUVirtualAddress);
        cl.SetComputeRootUnorderedAccessView(3, shadowPerDraws.GPUVirtualAddress);
        for (int i = 0; i < shadowSlices.Count; i++) {
            cl.SetComputeRootConstantBufferView(0, shadowCullParamUpload.GPUVirtualAddress + (ulong)(shadowCullParamSlot + (long)i * cullParamSlotSize));
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

    // Debug door (BALLISTIC_DX12_HIZ_DEBUG=1): read back the geometry command buffer after the cull and
    // count how many submeshes survived (InstanceCount == 1). Proves the GPU cull (frustum + Hi-Z) is
    // actually dropping draws, not silently passing everything. Blocks — debug only.
    public unsafe (int visible, int total) DebugVisibleCount() {
        int total = LastSubmeshes;
        if (total <= 0) return (0, 0);
        using ID3D12Resource rb = dev.CreateReadbackBuffer(total * DrawCmdStride);
        dev.ExecuteSync(cl => {
            cl.ResourceBarrierTransition(commands, ResourceStates.IndirectArgument, ResourceStates.CopySource);
            cl.CopyBufferRegion(rb, 0, commands, 0, (ulong)((long)total * DrawCmdStride));
            cl.ResourceBarrierTransition(commands, ResourceStates.CopySource, ResourceStates.IndirectArgument);
        });
        Span<uint> cmds = rb.Map<uint>(0, total * 6);   // 6 uints per command; [2] = InstanceCount
        int visible = 0;
        for (int i = 0; i < total; i++) if (cmds[i * 6 + 2] == 1u) visible++;
        rb.Unmap(0);
        return (visible, total);
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
    static Vector4 ToNum(Vector4 v) => v;

    public void Dispose() {
        cullRootSig?.Dispose(); geoCullRootSig?.Dispose(); cullPso?.Dispose(); drawRootSig?.Dispose(); drawPso?.Dispose();
        cmdSig?.Dispose(); commands?.Dispose(); perDraws?.Dispose(); cpuPerDraws?.Dispose();
        metaUpload?.Dispose(); cullParamUpload?.Dispose(); materials?.Dispose();
        hiz?.Dispose();
        shadowCullPso?.Dispose(); shadowDrawRootSig?.Dispose(); shadowDrawPso?.Dispose(); shadowCmdSig?.Dispose();
        shadowCommands?.Dispose(); shadowPerDraws?.Dispose(); shadowMetaUpload?.Dispose(); shadowCullParamUpload?.Dispose();
        // R3b compute skinning.
        skinRootSig?.Dispose(); skinPso?.Dispose(); skinCb?.Dispose();
        foreach (var sb in skinnedBuffers.Values) { sb.Pos?.Dispose(); sb.Normal?.Dispose(); sb.Tangent?.Dispose(); }
        // R4 meshlet pipeline.
        meshletRootSig?.Dispose(); meshletPso?.Dispose(); meshletCullCb?.Dispose(); Dx12Meshlet.Clear();
    }
}
