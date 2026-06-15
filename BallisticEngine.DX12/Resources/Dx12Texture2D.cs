using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// DX12 implementation of the engine's Texture2D. Uploads the (possibly mipped, possibly block-
// compressed) CPU pixel chain into a DEFAULT-heap texture via an upload heap + CopyTextureRegion per
// mip (the GetCopyableFootprints row-pitch dance, same as the offscreen readback in reverse), then
// creates ONE persistent SRV in Dx12Backend.SrvStore. The renderer copies that SRV into its per-draw
// shader-visible descriptor table — DX12 binds textures by descriptor, not by unit, so Activate/
// Deactivate are no-ops (the GL unit-binding model has no DX12 equivalent here).
public sealed class Dx12Texture2D : Texture2D {
    public override int UID { get; protected set; }
    static int nextId = 1;

    ID3D12Resource resource;
    int srvIndex = -1;
    public ID3D12Resource Resource => resource;
    // CPU handle of this texture's persistent SRV — the renderer copies it into the shader-visible heap.
    public CpuDescriptorHandle SrvCpu => Dx12Backend.SrvStore.Cpu(srvIndex);

    public Dx12Texture2D() {
        UID = nextId++;
    }

    // The engine's Upload is protected-internal; from this SEPARATE assembly DirectXRenderAsset only sees
    // `protected`, which it can't call (it isn't a subclass). This public wrapper is the factory's entry.
    public void UploadPublic(in TextureData data, TextureType type) => Upload(in data, type);

    // `protected` (not `protected internal`): cross-assembly override of the engine's protected-internal
    // Upload — the `internal` half isn't visible from the DX12 assembly (same C# rule as game scripts).
    protected override unsafe void Upload(in TextureData data, TextureType type) {
        Type = type;
        if (!data.IsValid)
            return;

        Format format = Dx12Backend.ToDxgi(data.Format, type);
        int mipCount = Math.Max(1, data.MipCount);

        var desc = ResourceDescription.Texture2D(format, (uint)data.Width, (uint)data.Height,
            arraySize: 1, mipLevels: (ushort)mipCount);
        resource = Dx12Backend.Device.Device.CreateCommittedResource(
            HeapProperties.DefaultHeapProperties, HeapFlags.None, desc, ResourceStates.CopyDest);
        resource.Name = $"Tex2D#{UID}({type})";

        // Footprints for every mip — DX12 demands 256-byte-aligned row pitches in the upload buffer.
        var footprints = new PlacedSubresourceFootPrint[mipCount];
        var rowCounts = new uint[mipCount];
        var rowSizes = new ulong[mipCount];
        Dx12Backend.Device.Device.GetCopyableFootprints(desc, 0, (uint)mipCount, 0,
            footprints, rowCounts, rowSizes, out ulong totalBytes);

        using ID3D12Resource upload = Dx12Backend.Device.Device.CreateCommittedResource(
            HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(totalBytes), ResourceStates.GenericRead);

        // Copy each mip's tightly-packed source rows into the aligned upload layout.
        byte* dst = upload.Map<byte>(0);
        fixed (byte* srcBase = data.Pixels) {
            for (int mip = 0; mip < mipCount; mip++) {
                PlacedSubresourceFootPrint fp = footprints[mip];
                long srcOffset = TextureMipLayout.LevelOffset(data.Width, data.Height, mip, data.Format);
                long srcRowBytes = (long)rowSizes[mip];   // packed bytes per row (or block-row)
                int rows = (int)rowCounts[mip];           // rows (or block-rows for BC)
                long dstRowPitch = fp.Footprint.RowPitch;
                byte* dstMip = dst + (long)fp.Offset;
                byte* srcMip = srcBase + srcOffset;
                for (int r = 0; r < rows; r++)
                    System.Buffer.MemoryCopy(
                        srcMip + r * srcRowBytes, dstMip + r * dstRowPitch,
                        srcRowBytes, srcRowBytes);
            }
        }
        upload.Unmap(0);

        Dx12Backend.Device.ExecuteSync(cl => {
            for (int mip = 0; mip < mipCount; mip++) {
                var d = new TextureCopyLocation(resource, (uint)mip);
                var s = new TextureCopyLocation(upload, footprints[mip]);
                cl.CopyTextureRegion(d, 0, 0, 0, s, null);
            }
            cl.ResourceBarrierTransition(resource, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
        });

        // Persistent SRV.
        srvIndex = Dx12Backend.SrvStore.Allocate();
        var srvDesc = new ShaderResourceViewDescription {
            Format = format,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = (uint)mipCount, MostDetailedMip = 0 },
        };
        Dx12Backend.Device.Device.CreateShaderResourceView(resource, srvDesc, Dx12Backend.SrvStore.Cpu(srvIndex));
    }

    public override void Activate() { /* DX12 binds by descriptor table at draw time */ }
    public override void Deactivate() { }

    public override void Dispose() {
        resource?.Dispose();
        resource = null;
    }
}
