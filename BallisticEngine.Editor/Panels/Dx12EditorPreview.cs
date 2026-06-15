using System.Runtime.InteropServices;
using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.DX12;
using Vortice.Direct3D12;
using Vortice.Dxc;
using Vortice.DXGI;
using GLVec3 = OpenTK.Mathematics.Vector3;
using SNM = System.Numerics;

namespace BallisticEngine.Editor;

// DX12 implementation of the editor's mesh/material thumbnail rendering + the CPU-pixel -> ImGui-texture
// upload. The GL MeshPreviewRenderer / MaterialPreviewRenderer / ThumbnailCache delegate here when the
// editor runs on DX12. Renders into a small Dx12OffscreenTarget and reads back RGBA8 (the GL path returns
// byte[] too, so the upstream thumbnail cache is unchanged); uploads go into a DEFAULT-heap texture whose
// SRV lives in the shared Dx12Backend.UiHeap, so the ImGui DX12 backend samples it as ImTextureID.
internal static class Dx12EditorPreview {
    static Dx12Device Dev => Dx12Backend.Device;
    static bool initialized;

    static ID3D12RootSignature meshRootSig, matRootSig;
    static ID3D12PipelineState meshPso, matPso;
    static ID3D12Resource meshCb, matCb;     // upload-heap CBVs (mapped)
    static unsafe byte* meshCbMapped, matCbMapped;
    static Dx12DescriptorHeap matSrvHeap;    // shader-visible, ring; 2 SRVs (albedo,normal) per material draw
    static ID3D12Resource whiteTex;          // 1x1 white fallback for missing material maps
    static int whiteSrvIndex = -1;

    // Unit UV sphere (interleaved pos/normal/uv, stride 32), built once for the material preview.
    static ID3D12Resource sphereVb, sphereIb;
    static int sphereIndexCount;

    static readonly Dictionary<int, Dx12OffscreenTarget> targets = new();   // by size

    [StructLayout(LayoutKind.Sequential)]
    struct MatConstants { public SNM.Matrix4x4 Mvp; public SNM.Vector4 BaseColor; public float Roughness, Metallic, HasAlbedo, HasNormal; }

    static unsafe void EnsureInitialized() {
        if (initialized) return;
        initialized = true;

        // --- Mesh preview pipeline: CBV b0 (mvp), pos@slot0 + normal@slot1 ---
        var meshCbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.Vertex);
        meshRootSig = Dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout, new[] { meshCbv })));
        string meshHlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("EditorMeshPreview.hlsl");
        meshPso = Dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = meshRootSig,
            VertexShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, meshHlsl, "VSMain", "EditorMeshPreview.hlsl"),
            PixelShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, meshHlsl, "PSMain", "EditorMeshPreview.hlsl"),
            InputLayout = new InputLayoutDescription(
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 0, 1)),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.Default,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.ColorFormat },
            DepthStencilFormat = Dx12OffscreenTarget.DepthFormat,
            SampleDescription = new SampleDescription(1, 0),
        });

        // --- Material preview pipeline: CBV b0 + SRV table t0..t1 + sampler s0; interleaved pos/normal/uv ---
        var matCbv = new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All);
        var matRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 2, baseShaderRegister: 0);
        var matTable = new RootParameter1(new RootDescriptorTable1(matRange), ShaderVisibility.Pixel);
        var samp = new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0) {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap, MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never, MinLOD = 0, MaxLOD = float.MaxValue,
        };
        matRootSig = Dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(
            new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout,
                new[] { matCbv, matTable }, new[] { samp })));
        string matHlsl = BallisticEngine.DX12.EmbeddedShaderSource.ReadHlsl("EditorMaterialPreview.hlsl");
        matPso = Dev.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription {
            RootSignature = matRootSig,
            VertexShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Vertex, matHlsl, "VSMain", "EditorMaterialPreview.hlsl"),
            PixelShader = Dx12ShaderCompiler.Compile(DxcShaderStage.Pixel, matHlsl, "PSMain", "EditorMaterialPreview.hlsl"),
            InputLayout = new InputLayoutDescription(
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 24, 0)),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, SampleMask = uint.MaxValue,
            RasterizerState = RasterizerDescription.CullNone, BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.Default,
            RenderTargetFormats = new[] { Dx12OffscreenTarget.ColorFormat },
            DepthStencilFormat = Dx12OffscreenTarget.DepthFormat,
            SampleDescription = new SampleDescription(1, 0),
        });

        meshCb = Dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(256), ResourceStates.GenericRead);
        meshCbMapped = meshCb.Map<byte>(0);
        matCb = Dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(256), ResourceStates.GenericRead);
        matCbMapped = matCb.Map<byte>(0);

        matSrvHeap = new Dx12DescriptorHeap(Dev,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 64, shaderVisible: true);

        CreateWhiteTexture();
        BuildSphere();
    }

    static unsafe void CreateWhiteTexture() {
        var desc = ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, 1, 1, arraySize: 1, mipLevels: 1);
        whiteTex = Dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            desc, ResourceStates.CopyDest);
        var fps = new PlacedSubresourceFootPrint[1]; var rc = new uint[1]; var rs = new ulong[1];
        Dev.Device.GetCopyableFootprints(desc, 0, 1, 0, fps, rc, rs, out ulong total);
        using ID3D12Resource upload = Dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties,
            HeapFlags.None, ResourceDescription.Buffer(total), ResourceStates.GenericRead);
        byte* p = upload.Map<byte>(0); p[0] = p[1] = p[2] = p[3] = 255; upload.Unmap(0);
        Dev.ExecuteUpload(cl => {
            cl.CopyTextureRegion(new TextureCopyLocation(whiteTex, 0), 0, 0, 0, new TextureCopyLocation(upload, fps[0]), null);
            cl.ResourceBarrierTransition(whiteTex, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
        });
        whiteSrvIndex = Dx12Backend.SrvStore.Allocate();
        Dev.Device.CreateShaderResourceView(whiteTex, new ShaderResourceViewDescription {
            Format = Format.R8G8B8A8_UNorm, ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        }, Dx12Backend.SrvStore.Cpu(whiteSrvIndex));
    }

    static Dx12OffscreenTarget TargetFor(int size) {
        if (!targets.TryGetValue(size, out var t)) {
            t = new Dx12OffscreenTarget(Dev, size, size, withDepth: true);
            targets[size] = t;
        }
        return t;
    }

    public static unsafe byte[] RenderMesh(in MeshData data, int size) {
        EnsureInitialized();
        (GLVec3 center, float radius) = Bounds(data.Vertices);
        SNM.Vector3 c = new(center.X, center.Y, center.Z);
        SNM.Vector3 dir = SNM.Vector3.Normalize(new SNM.Vector3(1f, 0.65f, 1.3f));
        SNM.Vector3 eye = c + dir * radius * 2.1f;
        SNM.Matrix4x4 view = SNM.Matrix4x4.CreateLookAt(eye, c, SNM.Vector3.UnitY);
        SNM.Matrix4x4 proj = SNM.Matrix4x4.CreatePerspectiveFieldOfView(
            40f * (MathF.PI / 180f), 1f, Math.Max(0.01f, radius * 0.1f), radius * 6f);
        *(SNM.Matrix4x4*)meshCbMapped = SNM.Matrix4x4.Transpose(view * proj);

        int vtxLen = data.Vertices.Length, normLen = data.Normals.Length, idxLen = data.Indices.Length;
        ID3D12Resource vb = Dev.CreateDefaultBuffer<GLVec3>(data.Vertices, ResourceStates.VertexAndConstantBuffer);
        ID3D12Resource nb = Dev.CreateDefaultBuffer<GLVec3>(data.Normals, ResourceStates.VertexAndConstantBuffer);
        ID3D12Resource ib = Dev.CreateDefaultBuffer<uint>(data.Indices, ResourceStates.IndexBuffer);
        Dx12OffscreenTarget target = TargetFor(size);
        target.RenderIntoCleared(0.16f, 0.16f, 0.17f, cl => {
            cl.SetGraphicsRootSignature(meshRootSig);
            cl.SetPipelineState(meshPso);
            cl.SetGraphicsRootConstantBufferView(0, meshCb.GPUVirtualAddress);
            cl.IASetVertexBuffers(0, new VertexBufferView(vb.GPUVirtualAddress, (uint)(vtxLen * 12), 12u));
            cl.IASetVertexBuffers(1, new VertexBufferView(nb.GPUVirtualAddress, (uint)(normLen * 12), 12u));
            cl.IASetIndexBuffer(new IndexBufferView(ib.GPUVirtualAddress, (uint)(idxLen * 4), Format.R32_UInt));
            cl.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
            cl.DrawIndexedInstanced((uint)idxLen, 1u, 0u, 0, 0u);
        });
        byte[] pixels = target.ReadColorRgba8();
        vb.Dispose(); nb.Dispose(); ib.Dispose();   // RenderIntoCleared + readback flushed the GPU
        return pixels;
    }

    public static unsafe byte[] RenderMaterial(MaterialDefinition material, int size) {
        EnsureInitialized();
        SNM.Matrix4x4 view = SNM.Matrix4x4.CreateLookAt(new SNM.Vector3(0, 0, 3f), SNM.Vector3.Zero, SNM.Vector3.UnitY);
        SNM.Matrix4x4 proj = SNM.Matrix4x4.CreatePerspectiveFieldOfView(35f * (MathF.PI / 180f), 1f, 0.1f, 10f);

        Dx12Texture2D albedo = Resolve(material, "Diffuse");
        Dx12Texture2D normal = Resolve(material, "Normal");
        var cb = new MatConstants {
            Mvp = SNM.Matrix4x4.Transpose(view * proj),
            BaseColor = BaseColorOf(material),
            Roughness = Math.Clamp(material.Roughness ?? 0.5f, 0f, 1f),
            Metallic = Math.Clamp(material.Metallic ?? 0f, 0f, 1f),
            HasAlbedo = albedo != null ? 1f : 0f,
            HasNormal = normal != null ? 1f : 0f,
        };
        *(MatConstants*)matCbMapped = cb;

        // 2 contiguous SRV slots (albedo, normal) in the ring heap; missing maps fall back to 1x1 white.
        int slot = matSrvHeap.AllocateRange(2);
        var heapType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Dev.Device.CopyDescriptorsSimple(1, matSrvHeap.Cpu(slot),
            albedo != null ? albedo.SrvCpu : Dx12Backend.SrvStore.Cpu(whiteSrvIndex), heapType);
        Dev.Device.CopyDescriptorsSimple(1, matSrvHeap.Cpu(slot + 1),
            normal != null ? normal.SrvCpu : Dx12Backend.SrvStore.Cpu(whiteSrvIndex), heapType);

        Dx12OffscreenTarget target = TargetFor(size);
        target.RenderIntoCleared(0.13f, 0.13f, 0.15f, cl => {
            cl.SetGraphicsRootSignature(matRootSig);
            cl.SetPipelineState(matPso);
            cl.SetDescriptorHeaps(matSrvHeap.Heap);
            cl.SetGraphicsRootConstantBufferView(0, matCb.GPUVirtualAddress);
            cl.SetGraphicsRootDescriptorTable(1, matSrvHeap.Gpu(slot));
            cl.IASetVertexBuffers(0, new VertexBufferView(sphereVb.GPUVirtualAddress, (uint)(sphereVertCount * 32), 32u));
            cl.IASetIndexBuffer(new IndexBufferView(sphereIb.GPUVirtualAddress, (uint)(sphereIndexCount * 4), Format.R32_UInt));
            cl.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
            cl.DrawIndexedInstanced((uint)sphereIndexCount, 1u, 0u, 0, 0u);
        });
        return target.ReadColorRgba8();
    }

    static Dx12Texture2D Resolve(MaterialDefinition m, string slot) {
        if (m.Textures is null || !m.Textures.TryGetValue(slot, out string reference) || string.IsNullOrEmpty(reference))
            return null;
        try {
            string path = reference.StartsWith("guid:", StringComparison.Ordinal) &&
                          Guid.TryParseExact(reference["guid:".Length..], "N", out Guid g)
                ? AssetDatabase.GuidToAssetPath(g) : reference;
            if (path is null) return null;
            // Only return a texture that actually has a valid SRV — a null/invalid SRV copied into the
            // shader-visible table is an out-of-bounds descriptor = GPU device-removal. Missing → white fallback.
            return AssetDatabase.Load<Texture2D>(path) is Dx12Texture2D t && t.HasSrv ? t : null;
        }
        catch { return null; }
    }

    static SNM.Vector4 BaseColorOf(MaterialDefinition m) => m.BaseColor switch {
        { Length: >= 4 } c => new SNM.Vector4(c[0], c[1], c[2], c[3]),
        { Length: 3 } c => new SNM.Vector4(c[0], c[1], c[2], 1f),
        _ => SNM.Vector4.One,
    };

    static (GLVec3 center, float radius) Bounds(GLVec3[] vertices) {
        GLVec3 min = vertices[0], max = vertices[0];
        foreach (GLVec3 v in vertices) { min = GLVec3.ComponentMin(min, v); max = GLVec3.ComponentMax(max, v); }
        GLVec3 center = (min + max) * 0.5f;
        float radius = Math.Max(0.01f, (max - min).Length * 0.5f);
        return (center, radius);
    }

    static int sphereVertCount;
    static unsafe void BuildSphere() {
        const int stacks = 32, slices = 48;
        var verts = new List<float>();
        var indices = new List<uint>();
        for (int i = 0; i <= stacks; i++) {
            float phi = MathF.PI * i / stacks;
            for (int j = 0; j <= slices; j++) {
                float theta = 2f * MathF.PI * j / slices;
                float x = MathF.Sin(phi) * MathF.Cos(theta), y = MathF.Cos(phi), z = MathF.Sin(phi) * MathF.Sin(theta);
                verts.Add(x); verts.Add(y); verts.Add(z);
                verts.Add(x); verts.Add(y); verts.Add(z);
                verts.Add((float)j / slices); verts.Add((float)i / stacks);
            }
        }
        int ring = slices + 1;
        for (int i = 0; i < stacks; i++)
            for (int j = 0; j < slices; j++) {
                uint a = (uint)(i * ring + j), b = (uint)((i + 1) * ring + j);
                indices.Add(a); indices.Add(b); indices.Add(a + 1);
                indices.Add(a + 1); indices.Add(b); indices.Add(b + 1);
            }
        sphereVertCount = verts.Count / 8;
        sphereIndexCount = indices.Count;
        sphereVb = Dev.CreateDefaultBuffer<float>(verts.ToArray(), ResourceStates.VertexAndConstantBuffer);
        sphereIb = Dev.CreateDefaultBuffer<uint>(indices.ToArray(), ResourceStates.IndexBuffer);
    }

    // A DX12 editor texture (thumbnail/preview): a DEFAULT-heap texture + its SRV in the shared UiHeap.
    // The Handle is the GPU descriptor ptr ImGui samples (ImTextureID). Dispose releases the resource AND
    // returns the UiHeap slot for reuse (so asset-browser invalidations don't leak descriptors).
    public sealed class Dx12EditorTexture : IDisposable {
        public ID3D12Resource Resource;
        public int UiSlot = -1;
        public nint Handle;
        public void Dispose() {
            Resource?.Dispose(); Resource = null;
            if (UiSlot >= 0) { Dx12Backend.UiHeap.Free(UiSlot); UiSlot = -1; }
        }
    }

    // Upload RGBA8 CPU pixels (size x size, top-down) into a fresh DX12 texture + UiHeap SRV.
    public static unsafe Dx12EditorTexture UploadTexture(byte[] pixels, int size) {
        EnsureInitialized();
        var tex = new Dx12EditorTexture();
        var desc = ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, (uint)size, (uint)size, arraySize: 1, mipLevels: 1);
        tex.Resource = Dev.Device.CreateCommittedResource(HeapProperties.DefaultHeapProperties, HeapFlags.None,
            desc, ResourceStates.CopyDest);
        var fps = new PlacedSubresourceFootPrint[1]; var rc = new uint[1]; var rs = new ulong[1];
        Dev.Device.GetCopyableFootprints(desc, 0, 1, 0, fps, rc, rs, out ulong total);
        long rowPitch = fps[0].Footprint.RowPitch;
        using ID3D12Resource upload = Dev.Device.CreateCommittedResource(HeapProperties.UploadHeapProperties,
            HeapFlags.None, ResourceDescription.Buffer(total), ResourceStates.GenericRead);
        byte* dst = upload.Map<byte>(0);
        for (int y = 0; y < size; y++)
            Marshal.Copy(pixels, y * size * 4, (IntPtr)(dst + y * rowPitch), size * 4);
        upload.Unmap(0);
        Dev.ExecuteUpload(cl => {
            cl.CopyTextureRegion(new TextureCopyLocation(tex.Resource, 0), 0, 0, 0, new TextureCopyLocation(upload, fps[0]), null);
            cl.ResourceBarrierTransition(tex.Resource, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
        });
        tex.UiSlot = Dx12Backend.UiHeap.Allocate();
        Dev.Device.CreateShaderResourceView(tex.Resource, new ShaderResourceViewDescription {
            Format = Format.R8G8B8A8_UNorm, ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        }, Dx12Backend.UiHeap.Cpu(tex.UiSlot));
        tex.Handle = (nint)Dx12Backend.UiHeap.Gpu(tex.UiSlot).Ptr;
        return tex;
    }
}
