using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// DX12 cubemap (the engine's Texture3D = an environment cube, used by skybox + IBL). Uploads 6 faces
// (+X,-X,+Y,-Y,+Z,-Z) into a TextureCube and creates a persistent cube SRV. Not on the Phase 2d minimal
// opaque path (sky/IBL come later), but wired so CreateCubemap returns a real resource and the IBL phase
// has its upload path ready. Faces are RGBA32F (HDR sky) or RGBA8.
public sealed class Dx12Texture3D : Texture3D {
    public override int UID { get; protected set; }
    static int nextId = 1;

    ID3D12Resource resource;
    int srvIndex = -1;
    public ID3D12Resource Resource => resource;
    public CpuDescriptorHandle SrvCpu => Dx12Backend.SrvStore.Cpu(srvIndex);

    public Dx12Texture3D() {
        UID = nextId++;
        Type = TextureType.SkyBox;
    }

    // Public entry for the factory (UploadFaces is protected from this assembly — see Dx12Texture2D).
    public void UploadFacesPublic(TextureData[] faces) => UploadFaces(faces);

    // `protected` (not `protected internal`): cross-assembly override rule (see Dx12Texture2D.Upload).
    protected override unsafe void UploadFaces(TextureData[] faces) {
        if (faces is null || faces.Length != 6 || !faces[0].IsValid)
            return;

        TextureData f0 = faces[0];
        Format format = Dx12Backend.ToDxgi(f0.Format, TextureType.SkyBox);
        var desc = ResourceDescription.Texture2D(format, (uint)f0.Width, (uint)f0.Height,
            arraySize: 6, mipLevels: 1);
        resource = Dx12Backend.Device.Device.CreateCommittedResource(
            HeapProperties.DefaultHeapProperties, HeapFlags.None, desc, ResourceStates.CopyDest);
        resource.Name = $"Cube#{UID}";

        // Footprints for all 6 subresources (one mip each).
        var footprints = new PlacedSubresourceFootPrint[6];
        var rowCounts = new uint[6];
        var rowSizes = new ulong[6];
        Dx12Backend.Device.Device.GetCopyableFootprints(desc, 0, 6, 0,
            footprints, rowCounts, rowSizes, out ulong totalBytes);

        using ID3D12Resource upload = Dx12Backend.Device.Device.CreateCommittedResource(
            HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(totalBytes), ResourceStates.GenericRead);

        byte* dst = upload.Map<byte>(0);
        for (int face = 0; face < 6; face++) {
            PlacedSubresourceFootPrint fp = footprints[face];
            long srcRowBytes = (long)rowSizes[face];
            int rows = (int)rowCounts[face];
            long dstPitch = fp.Footprint.RowPitch;
            byte* dstFace = dst + (long)fp.Offset;
            fixed (byte* src = faces[face].Pixels) {
                for (int r = 0; r < rows; r++)
                    System.Buffer.MemoryCopy(src + r * srcRowBytes, dstFace + r * dstPitch,
                        srcRowBytes, srcRowBytes);
            }
        }
        upload.Unmap(0);

        Dx12Backend.Device.ExecuteSync(cl => {
            for (int face = 0; face < 6; face++) {
                var d = new TextureCopyLocation(resource, (uint)face);
                var s = new TextureCopyLocation(upload, footprints[face]);
                cl.CopyTextureRegion(d, 0, 0, 0, s, null);
            }
            cl.ResourceBarrierTransition(resource, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
        });

        srvIndex = Dx12Backend.SrvStore.Allocate();
        var srvDesc = new ShaderResourceViewDescription {
            Format = format,
            ViewDimension = ShaderResourceViewDimension.TextureCube,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            TextureCube = new TextureCubeShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        };
        Dx12Backend.Device.Device.CreateShaderResourceView(resource, srvDesc, Dx12Backend.SrvStore.Cpu(srvIndex));
    }

    public override void Activate() { }
    public override void Deactivate() { }
    public override void Dispose() {
        resource?.Dispose();
        resource = null;
    }
}
