using Vortice.Direct3D12;
using Vortice.Dxc;
using GLMatrix4 = System.Numerics.Matrix4x4;
using GLVector3 = System.Numerics.Vector3;

namespace BallisticEngine.DX12;

public sealed class Dx12GpuDrivenRenderer : IDisposable {
    const int Capacity = 8192;
    const int MaxGroups = 64;
    const int DrawCmdStride = 24;

    readonly Dx12Device dev;

    ID3D12RootSignature cullRootSig;
    ID3D12RootSignature geoCullRootSig;
    ID3D12PipelineState cullPso;
    Dx12HiZ hiz;
    int hizBindlessIndex = -1;
    bool hizOnThisFrame;

    public int HizBindlessIndex => hizBindlessIndex;
    public bool HizOn => hizOnThisFrame && hizBindlessIndex >= 0;
    public int HizWidth => hiz?.Width ?? 0;
    public int HizHeight => hiz?.Height ?? 0;
    public int HizMipCount => hiz?.MipCount ?? 0;
    ID3D12RootSignature drawRootSig;
    ID3D12PipelineState drawPso;
    ID3D12CommandSignature cmdSig;

    ID3D12Resource metaUpload;      unsafe byte* metaMapped;
    ID3D12Resource cullParamUpload; unsafe byte* cullParamMapped;
    ID3D12Resource commands;
    ID3D12Resource perDraws;
    ID3D12Resource materials;       unsafe byte* materialsMapped;

    ID3D12Resource cpuPerDraws;     unsafe byte* cpuPerDrawsMapped;
    long cpuPerDrawsFrameStride;
    const int MaxCpuDraws = 8192;

    ID3D12RootSignature skinRootSig;
    ID3D12PipelineState skinPso;
    ID3D12RootSignature meshletRootSig;
    ID3D12PipelineState meshletPso;

    public sealed class SkinnedBuffers {
        public ID3D12Resource Pos, Normal, Tangent; public int VertexCount;
        public ResourceStates State = ResourceStates.UnorderedAccess;
    }
    readonly Dictionary<IStaticMeshRenderer, SkinnedBuffers> skinnedBuffers = new();
    int cullParamSlotSize;
    int geoCullParamSlotSize;

    int metaStride, perDrawStride, materialStride;

    long metaFrameStride;
    long cullParamFrameStride;
    long materialsFrameStride;
    public long LastTris;
    public int LastSubmeshes;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct SubmeshMeta {
        public Matrix4x4 Mvp; public Matrix4x4 Model;
        public Vector4 AabbMin; public Vector4 AabbMax;
        public uint FirstIndex, IndexCount, MaterialId, Flags;
        public uint LodCount; public float LodBias; public uint Lp0, Lp1;
        public uint LodR0a, LodR0b, LodR1a, LodR1b, LodR2a, LodR2b, LodR3a, LodR3b;
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

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct GeoCullParams {
        public Vector4 P0, P1, P2, P3, P4, P5;
        public uint SubmeshCount, OutBase, HizEnabled, HizIndex;
        public Matrix4x4 ViewProj;
        public Matrix4x4 View;
        public Vector4 HizParams;
        public Vector4 HizFar;
        public Vector4 LodSpanThresholds;
        public Vector4 LodControl;
    }

    readonly Dictionary<Material, int> materialIds = new();
    readonly Dictionary<Dx12Texture2D, int> bindlessIds = new();
    int materialCount;
    int tableStamp = -1;

    const int ShadowCascades = 4;
    const int ShadowCapacity = ShadowCascades * Capacity;
    ID3D12PipelineState shadowCullPso;
    ID3D12RootSignature shadowDrawRootSig;
    ID3D12PipelineState shadowDrawPso;
    ID3D12CommandSignature shadowCmdSig;
    ID3D12Resource shadowMetaUpload;   unsafe byte* shadowMetaMapped;
    ID3D12Resource shadowCullParamUpload; unsafe byte* shadowCullParamMapped;
    ID3D12Resource shadowCommands;
    ID3D12Resource shadowPerDraws;
    int shadowMetaStride;
    long shadowMetaFrameStride;
    long shadowCullParamFrameStride;
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
        var cullParams = new[] {
            new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All),
            new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All),
            new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(0, 0), ShaderVisibility.All),
            new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(1, 0), ShaderVisibility.All),
        };
        cullRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, cullParams)));

        var pointClamp = new StaticSamplerDescription(ShaderVisibility.All, 0, 0) {
            Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        geoCullRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(
                RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
                cullParams, new[] { pointClamp })));

        var skinParams = new[] {
            new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All),
            new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(0, 0), ShaderVisibility.All), new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(1, 0), ShaderVisibility.All), new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(2, 0), ShaderVisibility.All), new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(3, 0), ShaderVisibility.All), new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(4, 0), ShaderVisibility.All), new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1(5, 0), ShaderVisibility.All), new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(0, 0), ShaderVisibility.All), new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(1, 0), ShaderVisibility.All), new RootParameter1(RootParameterType.UnorderedAccessView, new RootDescriptor1(2, 0), ShaderVisibility.All),
        };
        skinRootSig = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.None, skinParams)));
        skinPso = dev.CreateComputePso(new ComputePipelineStateDescription {
            RootSignature = skinRootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute,
                EmbeddedShaderSource.ReadHlsl("SkinCompute.hlsl"), "CSMain", "SkinCompute.hlsl"),
        }, "GpuDriven.Skin");

        if (dev.HasMeshShaders) {
            var mp = new System.Collections.Generic.List<RootParameter1> {
                new(new RootConstants(0, 0, 4), ShaderVisibility.All),
            };
            for (int t = 0; t <= 9; t++) mp.Add(new RootParameter1(RootParameterType.ShaderResourceView, new RootDescriptor1((uint)t, 0), ShaderVisibility.All));
            mp.Add(new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(1, 0), ShaderVisibility.All));
            mp.Add(new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(2, 0), ShaderVisibility.All));
            var msWrap = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
                Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap,
                AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 16, ComparisonFunction = ComparisonFunction.Never,
                MinLOD = 0, MaxLOD = float.MaxValue,
            };
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
        cullPso = dev.CreateComputePso(new ComputePipelineStateDescription {
            RootSignature = geoCullRootSig,
            ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, cullHlsl, "CSMain", "GpuCull.hlsl"),
        }, "GpuDriven.Cull");

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
        drawPso = dev.CreateGraphicsPso(new GraphicsPipelineStateDescription {
            RootSignature = drawRootSig, VertexShader = vs, PixelShader = ps, InputLayout = layout,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullClockwise,
            BlendState = BlendDescription.Opaque, DepthStencilState = DepthStencilDescription.Default,
            RenderTargetFormats = Dx12GBuffer.ColorFormats,
            DepthStencilFormat = Dx12GBuffer.DepthFormat, SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
        }, "GpuDriven.Draw");

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

        cpuPerDrawsFrameStride = (long)perDrawStride * MaxCpuDraws;
        cpuPerDraws = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(cpuPerDrawsFrameStride * dev.FramesInFlight)), ResourceStates.GenericRead);
        cpuPerDrawsMapped = cpuPerDraws.Map<byte>(0);
    }

    public void Invalidate() {
        tableStamp = -1;
        materialIds.Clear();
        bindlessIds.Clear();
        materialCount = 0;
        hizBindlessIndex = -1;
        bufferBindless.Clear();
        visResolveUavBase = -1;
        hiz?.Invalidate();
    }

    public unsafe void EnsureMaterialTable(List<IStaticMeshRenderer> wholeMesh) {
        int stamp = wholeMesh.Count;
        foreach (var r in wholeMesh) { var m = r.SharedMesh; if (m != null) stamp = stamp * 31 + m.SubMeshes.Length; }
        if (stamp == tableStamp) return;
        tableStamp = stamp;

        Dx12Backend.BindlessHeap.Reset();
        materialIds.Clear();
        bindlessIds.Clear();
        bufferBindless.Clear();
        visResolveUavBase = -1;
        materialCount = 0;
        hizBindlessIndex = -1;

        foreach (var r in wholeMesh) {
            Mesh mesh = r.SharedMesh; if (mesh is null) continue;
            for (int s = 0; s < mesh.SubMeshes.Length; s++)
                RegisterMaterial(r.MaterialFor(s));
        }
    }

    const int MaxMaterials = 4096;

    unsafe void RegisterMaterial(Material mat) {
        if (mat is null || mat.Transparent || materialIds.ContainsKey(mat) || materialCount >= MaxMaterials) return;
        int id = materialCount++;
        materialIds[mat] = id;
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
            EmissiveFactor = new Vector4(ec.X, ec.Y, ec.Z, 0f) * ei,
            Metallic = mat.GetFloat(MaterialSemantic.MetallicFactor), Roughness = mat.GetFloat(MaterialSemantic.RoughnessFactor),
            SpecularReflectance = mat.GetFloat(MaterialSemantic.SpecularReflectance), NormalStrength = mat.GetFloat(MaterialSemantic.NormalStrength),
            NormalFlipY = mat.GetFloat(MaterialSemantic.NormalFlipY), HasMetallicMap = hasMetal ? 1f : 0f,
            HasRoughnessMap = hasRough ? 1f : 0f, PackedOrm = mat.GetFloat(MaterialSemantic.PackedOrm),
            Cutout = mat.GetFloat(MaterialSemantic.Cutout), HasEmissive = mat.GetFloat(MaterialSemantic.IsEmissive),
        };
        for (int f = 0; f < dev.FramesInFlight; f++)
            *(GpuMaterial*)(materialsMapped + f * materialsFrameStride + (long)id * materialStride) = gm;
    }

    public int ResolveOrRegisterMaterialId(Material mat) {
        if (mat is null || mat.Transparent) return -1;
        if (materialIds.TryGetValue(mat, out int id)) return id;
        RegisterMaterial(mat);
        return materialIds.TryGetValue(mat, out id) ? id : -1;
    }

    public ulong MaterialsGpuAddress => materials is null ? 0 : materials.GPUVirtualAddress + (ulong)(dev.FrameSlot * materialsFrameStride);
    public int MaterialCount => materialCount;

    public void CpuBindlessBegin(ID3D12GraphicsCommandList4 cl, ulong motionCbAddress) {
        cl.SetDescriptorHeaps(Dx12Backend.BindlessHeap.Heap);
        cl.SetGraphicsRootSignature(drawRootSig);
        cl.SetPipelineState(drawPso);
        cl.SetGraphicsRootShaderResourceView(1, cpuPerDraws.GPUVirtualAddress + (ulong)(dev.FrameSlot * cpuPerDrawsFrameStride));
        cl.SetGraphicsRootShaderResourceView(2, MaterialsGpuAddress);
        cl.SetGraphicsRootConstantBufferView(3, motionCbAddress);
    }

    ID3D12Resource skinCb; unsafe byte* skinCbMapped; int skinCbStride; long skinCbFrameStride;
    const int MaxSkinnedPerFrame = 256;
    unsafe void EnsureSkinCb() {
        if (skinCb != null) return;
        skinCbStride = 256;
        skinCbFrameStride = (long)skinCbStride * MaxSkinnedPerFrame;
        skinCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(skinCbFrameStride * dev.FramesInFlight)), ResourceStates.GenericRead);
        skinCbMapped = skinCb.Map<byte>(0);
    }

    SkinnedBuffers EnsureSkinnedBuffers(IStaticMeshRenderer r, int vertexCount) {
        if (!skinnedBuffers.TryGetValue(r, out var sb)) { sb = new SkinnedBuffers(); skinnedBuffers[r] = sb; }
        if (sb.VertexCount == vertexCount && sb.Pos != null) return sb;
        if (sb.Pos != null) { dev.DeferredRelease(sb.Pos); dev.DeferredRelease(sb.Normal); dev.DeferredRelease(sb.Tangent); }
        ID3D12Resource MakeUav(int bytes) => dev.Device.CreateCommittedResource(
            HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)bytes, ResourceFlags.AllowUnorderedAccess), ResourceStates.UnorderedAccess);
        sb.Pos = MakeUav(vertexCount * 12);
        sb.Normal = MakeUav(vertexCount * 12);
        sb.Tangent = MakeUav(vertexCount * 16);
        sb.VertexCount = vertexCount;
        return sb;
    }

    public unsafe SkinnedBuffers DispatchSkin(ID3D12GraphicsCommandList4 cl, IStaticMeshRenderer r, int skinIndex,
        ulong boneGpuAddr, ulong inPos, ulong inNormal, ulong inTangent, ulong inBoneIdx, ulong inBoneWt,
        int vertexCount) {
        if (vertexCount <= 0 || skinIndex >= MaxSkinnedPerFrame) return null;
        EnsureSkinCb();
        var sb = EnsureSkinnedBuffers(r, vertexCount);
        long cbOff = (long)dev.FrameSlot * skinCbFrameStride + (long)skinIndex * skinCbStride;
        *(uint*)(skinCbMapped + cbOff) = (uint)vertexCount;
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
        Barrier(cl, sb.Pos, ResourceStates.UnorderedAccess, ResourceStates.VertexAndConstantBuffer);
        Barrier(cl, sb.Normal, ResourceStates.UnorderedAccess, ResourceStates.VertexAndConstantBuffer);
        Barrier(cl, sb.Tangent, ResourceStates.UnorderedAccess, ResourceStates.VertexAndConstantBuffer);
        sb.State = ResourceStates.VertexAndConstantBuffer;
        return sb;
    }

    static void Barrier(ID3D12GraphicsCommandList4 cl, ID3D12Resource res, ResourceStates from, ResourceStates to) {
        if (from != to) cl.ResourceBarrierTransition(res, from, to);
    }

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

    public int MaterialTableStamp => tableStamp;

    int Bindless(Texture2D tex, TextureType type) {
        var dx = (tex as Dx12Texture2D) ?? (DefaultTextures.Neutral(type) as Dx12Texture2D);
        if (dx is null) return 0;
        if (bindlessIds.TryGetValue(dx, out int idx)) return idx;
        idx = Dx12Backend.RegisterBindless(dx.SrvCpu);
        bindlessIds[dx] = idx;
        return idx;
    }

    public void BuildHiZ(CpuDescriptorHandle depthSrvCpu, int w, int h, bool enabled) {
        hizOnThisFrame = enabled;
        if (!enabled) return;
        bool recreated = hiz.Ensure(w, h);
        bool slotReallocated = hizBindlessIndex < 0;
        if (slotReallocated)
            hizBindlessIndex = Dx12Backend.BindlessHeap.Allocate();
        if (recreated || slotReallocated)
            hiz.CreateAllMipsSrv(Dx12Backend.BindlessHeap.Cpu(hizBindlessIndex));
        dev.ExecuteSync(cl => hiz.Build(cl, depthSrvCpu));
    }

    ID3D12Resource meshletCullCb; unsafe byte* meshletCullCbMapped; long meshletCullCbStride;
    unsafe void EnsureMeshletCullCb() {
        if (meshletCullCb != null) return;
        meshletCullCbStride = 512;
        meshletCullCb = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(meshletCullCbStride * dev.FramesInFlight)), ResourceStates.GenericRead);
        meshletCullCbMapped = meshletCullCb.Map<byte>(0);
    }

    public bool MeshletAvailable => meshletPso != null;
    public long MeshletTris => meshletTris;
    long meshletTris;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct VisDraw {
        public Matrix4x4 Mvp; public Matrix4x4 Model;
        public uint MaterialId;
        public uint PosIdx, NrmIdx, UvIdx, TanIdx;
        public uint MeshletIdx, MeshletVertIdx, MeshletPrimIdx;
    }
    ID3D12Resource visDraws; unsafe byte* visDrawsMapped; int visDrawStride; long visDrawsFrameStride;

    readonly Dictionary<ID3D12Resource, int> bufferBindless = new();
    unsafe void EnsureVisDraws() {
        if (visDraws != null) return;
        visDrawStride = System.Runtime.InteropServices.Marshal.SizeOf<VisDraw>();
        visDrawsFrameStride = (long)visDrawStride * MaxCpuDraws;
        visDraws = dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer((ulong)(visDrawsFrameStride * dev.FramesInFlight)), ResourceStates.GenericRead);
        visDrawsMapped = visDraws.Map<byte>(0);
    }
    public ulong VisDrawsAddress { get { EnsureVisDraws(); return visDraws.GPUVirtualAddress + (ulong)(dev.FrameSlot * visDrawsFrameStride); } }

    int visResolveUavBase = -1;
    public int ReserveVisResolveUavs() {
        if (visResolveUavBase >= 0) return visResolveUavBase;
        int first = Dx12Backend.BindlessHeap.Allocate();
        for (int i = 1; i < Dx12GBuffer.RtCount; i++) Dx12Backend.BindlessHeap.Allocate();
        visResolveUavBase = first;
        return first;
    }

    int RegisterBufferBindless(ID3D12Resource res, int count, int stride) {
        if (res is null) return 0;
        if (bufferBindless.TryGetValue(res, out int idx)) return idx;
        idx = Dx12Backend.BindlessHeap.Allocate();
        dev.Device.CreateShaderResourceView(res, new ShaderResourceViewDescription {
            Format = Vortice.DXGI.Format.Unknown, ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Buffer,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Buffer = new BufferShaderResourceView {
                FirstElement = 0, NumElements = (uint)count, StructureByteStride = (uint)stride,
                Flags = BufferShaderResourceViewFlags.None,
            },
        }, Dx12Backend.BindlessHeap.Cpu(idx));
        bufferBindless[res] = idx;
        return idx;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct MeshletDrawConst { public uint DrawIndex, MeshletBase, MeshletCount, Pad; }

    public ulong CpuPerDrawsAddress => cpuPerDraws.GPUVirtualAddress + (ulong)(dev.FrameSlot * cpuPerDrawsFrameStride);
    public ulong MeshletCullCbAddress { get { EnsureMeshletCullCb(); return meshletCullCb.GPUVirtualAddress + (ulong)(dev.FrameSlot * meshletCullCbStride); } }

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

        EnsureVisDraws();
        cl.SetDescriptorHeaps(Dx12Backend.BindlessHeap.Heap);
        cl.SetGraphicsRootSignature(visRootSig);
        cl.SetPipelineState(visPso);
        cl.SetGraphicsRootShaderResourceView(1, CpuPerDrawsAddress);
        cl.SetGraphicsRootConstantBufferView(12, meshletCullCb.GPUVirtualAddress + (ulong)cullOff);
        long visSlot = (long)dev.FrameSlot * visDrawsFrameStride;
        int draws = 0;
        foreach (var r in renderers) {
            Mesh mesh = r.SharedMesh; if (mesh is null) continue;
            var pos = mesh.VertexBuffer as Dx12Buffer<GLVector3>;
            var nrm = mesh.NormalBuffer as Dx12Buffer<GLVector3>;
            var uv  = mesh.UvBuffer as Dx12Buffer<Vector2>;
            var tan = mesh.TangentBuffer as Dx12Buffer<Vector4>;
            if (pos?.Resource is null || nrm?.Resource is null || uv?.Resource is null || tan?.Resource is null) continue;
            Matrix4x4 model = ToNum(r.Transform.WorldMatrix);
            Matrix4x4 mvp = model * viewProj;
            int only = r.SubMeshIndex;
            int sFirst = only >= 0 ? only : 0;
            int sLast = only >= 0 ? only : mesh.SubMeshes.Length - 1;
            cl.SetGraphicsRootShaderResourceView(7, pos.GpuAddress);
            int vcount = mesh.Vertices.Length;
            int posIdx = RegisterBufferBindless(pos.Resource, vcount, 12);
            int nrmIdx = RegisterBufferBindless(nrm.Resource, vcount, 12);
            int uvIdx  = RegisterBufferBindless(uv.Resource, vcount, 8);
            int tanIdx = RegisterBufferBindless(tan.Resource, vcount, 16);
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
                *(VisDraw*)(visDrawsMapped + visSlot + (long)cpuDrawIndex * visDrawStride) = new VisDraw {
                    Mvp = Matrix4x4.Transpose(mvp), Model = Matrix4x4.Transpose(model), MaterialId = (uint)mid,
                    PosIdx = (uint)posIdx, NrmIdx = (uint)nrmIdx, UvIdx = (uint)uvIdx, TanIdx = (uint)tanIdx,
                    MeshletIdx = (uint)RegisterBufferBindless(ml.Meshlets, ml.MeshletCount, 16),
                    MeshletVertIdx = (uint)RegisterBufferBindless(ml.Verts, ml.VertCount, 4),
                    MeshletPrimIdx = (uint)RegisterBufferBindless(ml.Prims, ml.PrimCount, 4),
                };
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
        long cullOff = (long)dev.FrameSlot * meshletCullCbStride;
        byte* cb = meshletCullCbMapped + cullOff;
        var planesDst = (Vector4*)cb;
        for (int i = 0; i < 6; i++) planesDst[i] = frustumPlanes[i];
        planesDst[6] = new Vector4(cameraPos, coneCull ? 1f : 0f);
        long o = 7 * 16;
        *(Matrix4x4*)(cb + o) = Matrix4x4.Transpose(viewProjUnjittered); o += 64;
        *(Matrix4x4*)(cb + o) = Matrix4x4.Transpose(view); o += 64;
        bool hizOn = hizOnThisFrame && hizBindlessIndex >= 0;
        *(Vector4*)(cb + o) = new Vector4(hiz.Width, hiz.Height, hiz.MipCount, near); o += 16;
        *(Vector4*)(cb + o) = new Vector4(far, hizOn ? 1f : 0f, Math.Max(hizBindlessIndex, 0), 0f);

        cl.SetDescriptorHeaps(Dx12Backend.BindlessHeap.Heap);
        cl.SetGraphicsRootSignature(meshletRootSig);
        cl.SetPipelineState(meshletPso);
        cl.SetGraphicsRootShaderResourceView(1, cpuPerDraws.GPUVirtualAddress + (ulong)(dev.FrameSlot * cpuPerDrawsFrameStride));
        cl.SetGraphicsRootShaderResourceView(2, MaterialsGpuAddress);
        cl.SetGraphicsRootConstantBufferView(11, motionCbAddress);
        cl.SetGraphicsRootConstantBufferView(12, meshletCullCb.GPUVirtualAddress + (ulong)cullOff);

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
                long pdOff = (long)dev.FrameSlot * cpuPerDrawsFrameStride + (long)cpuDrawIndex * perDrawStride;
                *(PerDraw*)(cpuPerDrawsMapped + pdOff) = new PerDraw {
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
                meshletTris += sub.IndexCount / 3;
            }
        }
        return draws;
    }

    public unsafe int RenderInto(ID3D12GraphicsCommandList4 cl, List<IStaticMeshRenderer> wholeMesh,
                                 Matrix4x4 viewProj, Vector4[] frustumPlanes,
                                 Matrix4x4 viewProjUnjittered, Matrix4x4 view, float near, float far,
                                 ulong motionCbAddress) {
        var groups = new List<(Dx12Buffer<GLVector3> vb, Dx12Buffer<GLVector3> nb,
            Dx12Buffer<Vector2> ub, Dx12Buffer<Vector4> tb,
            Dx12IndexBuffer ib, int baseIdx, int count)>();
        var meshOrder = new List<Mesh>();
        var byMesh = new Dictionary<Mesh, List<IStaticMeshRenderer>>();
        foreach (var r in wholeMesh) {
            Mesh m = r.SharedMesh; if (m is null) continue;
            if (!byMesh.TryGetValue(m, out var list)) { list = new(); byMesh[m] = list; meshOrder.Add(m); }
            list.Add(r);
        }

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
                int only = r.SubMeshIndex;
                int sFirst = only >= 0 ? only : 0;
                int sLast = only >= 0 ? only : mesh.SubMeshes.Length - 1;
                for (int s = sFirst; s <= sLast && total < Capacity; s++) {
                    if ((uint)s >= (uint)mesh.SubMeshes.Length) break;
                    SubMeshData sub = mesh.SubMeshes[s];
                    if (sub.IndexCount <= 0) continue;
                    Material mat = r.MaterialFor(s);
                    if (mat is null || mat.Transparent) continue;
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
                    if (sub.Lods is { Length: > 1 } lods) {
                        int n = Math.Min(lods.Length, 5);
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

        cl.ResourceBarrierTransition(commands, ResourceStates.IndirectArgument, ResourceStates.UnorderedAccess);
        cl.ResourceBarrierTransition(perDraws, ResourceStates.NonPixelShaderResource, ResourceStates.UnorderedAccess);

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

        cl.ResourceBarrierTransition(commands, ResourceStates.UnorderedAccess, ResourceStates.IndirectArgument);
        cl.ResourceBarrierTransition(perDraws, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);

        cl.SetDescriptorHeaps(Dx12Backend.BindlessHeap.Heap);
        cl.SetGraphicsRootSignature(drawRootSig);
        cl.SetPipelineState(drawPso);
        cl.SetGraphicsRootShaderResourceView(1, perDraws.GPUVirtualAddress);
        cl.SetGraphicsRootShaderResourceView(2, materials.GPUVirtualAddress + (ulong)(dev.FrameSlot * materialsFrameStride));
        cl.SetGraphicsRootConstantBufferView(3, motionCbAddress);
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
            cl.ExecuteIndirect(cmdSig, (uint)gp.count, commands, (ulong)((long)gp.baseIdx * DrawCmdStride), null, 0);
        }
        LastTris = triAccum;
        LastSubmeshes = total;
        return groups.Count;
    }

    unsafe void BuildShadowPipelines() {
        string cullHlsl = EmbeddedShaderSource.ReadHlsl("GpuCullShadow.hlsl");
        shadowCullPso = dev.CreateComputePso(new ComputePipelineStateDescription {
            RootSignature = cullRootSig, ComputeShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Compute, cullHlsl, "CSMain", "GpuCullShadow.hlsl"),
        }, "GpuDriven.ShadowCull");

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
        var raster = RasterizerDescription.CullClockwise;
        raster.DepthBias = 2000; raster.SlopeScaledDepthBias = 2.5f; raster.DepthBiasClamp = 0f;
        shadowDrawPso = dev.CreateGraphicsPso(new GraphicsPipelineStateDescription {
            RootSignature = shadowDrawRootSig, VertexShader = vs, PixelShader = default, InputLayout = layout,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = raster, BlendState = BlendDescription.Opaque, DepthStencilState = DepthStencilDescription.Default,
            RenderTargetFormats = Array.Empty<Vortice.DXGI.Format>(),
            DepthStencilFormat = Dx12ShadowMap.DepthFormat, SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
        }, "GpuDriven.ShadowDraw");

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

    public unsafe void BuildShadowCull(ID3D12GraphicsCommandList4 cl, List<IStaticMeshRenderer> wholeMesh,
                                       Matrix4x4[] cascadeMatrices, int activeCascades = ShadowCascades) {
        shadowSlices.Clear();
        int cascades = Math.Clamp(activeCascades, 1, ShadowCascades);
        var meshOrder = new List<Mesh>();
        var byMesh = new Dictionary<Mesh, List<IStaticMeshRenderer>>();
        foreach (var r in wholeMesh) {
            Mesh m = r.SharedMesh; if (m is null) continue;
            if (!byMesh.TryGetValue(m, out var list)) { list = new(); byMesh[m] = list; meshOrder.Add(m); }
            list.Add(r);
        }

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

    public unsafe (int visible, int total) DebugVisibleCount() {
        int total = LastSubmeshes;
        if (total <= 0) return (0, 0);
        using ID3D12Resource rb = dev.CreateReadbackBuffer(total * DrawCmdStride);
        dev.ExecuteSync(cl => {
            cl.ResourceBarrierTransition(commands, ResourceStates.IndirectArgument, ResourceStates.CopySource);
            cl.CopyBufferRegion(rb, 0, commands, 0, (ulong)((long)total * DrawCmdStride));
            cl.ResourceBarrierTransition(commands, ResourceStates.CopySource, ResourceStates.IndirectArgument);
        });
        Span<uint> cmds = rb.Map<uint>(0, total * 6);
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
        skinRootSig?.Dispose(); skinPso?.Dispose(); skinCb?.Dispose();
        foreach (var sb in skinnedBuffers.Values) { sb.Pos?.Dispose(); sb.Normal?.Dispose(); sb.Tangent?.Dispose(); }

        meshletRootSig?.Dispose(); meshletPso?.Dispose(); meshletCullCb?.Dispose(); Dx12Meshlet.Clear();
    }
}
