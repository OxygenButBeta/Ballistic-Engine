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
        // Whole create+map+copy sequence serialized via the device gate (asset loading runs on worker
        // threads; concurrent CreateCommittedResource E_FAILs under heavy parallel load — see Dx12Device).
        // Can't capture `in`/ref TextureData in a lambda, so copy to a local first.
        TextureData d = data;   // can't capture `in` params in a lambda
        Dx12Backend.Device.RunExclusive(() => UploadCore(in d, type));
    }

    unsafe void UploadCore(in TextureData data, TextureType type) {
        Type = type;
        if (!data.IsValid)
            return;

        Format format = Dx12Backend.ToDxgi(data.Format, type);

        // MIP 0 ONLY for now (first light). The full pre-baked BC mip chain upload had a copy bug that
        // surfaced as E_FAIL on 2048² BC1/BC3 with 12 mips (the sub-4×4 block-row math vs the D3D
        // footprint); uploading just the base level is guaranteed correct and gets the scene rendering.
        // Full mip-chain upload (better minification quality) is a tracked follow-up.
        const int mipCount = 1;

        var desc = ResourceDescription.Texture2D(format, (uint)data.Width, (uint)data.Height,
            arraySize: 1, mipLevels: mipCount);
        resource = Dx12Backend.Device.Device.CreateCommittedResource(
            HeapProperties.DefaultHeapProperties, HeapFlags.None, desc, ResourceStates.CopyDest);
        resource.Name = $"Tex2D#{UID}({type})";

        // Footprint of mip 0 (256-byte-aligned row pitch in the upload buffer).
        var footprints = new PlacedSubresourceFootPrint[1];
        var rowCounts = new uint[1];
        var rowSizes = new ulong[1];
        Dx12Backend.Device.Device.GetCopyableFootprints(desc, 0, 1, 0,
            footprints, rowCounts, rowSizes, out ulong totalBytes);
        PlacedSubresourceFootPrint fp = footprints[0];

        using ID3D12Resource upload = Dx12Backend.Device.Device.CreateCommittedResource(
            HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(totalBytes), ResourceStates.GenericRead);

        // Copy mip 0's tightly-packed source rows into the aligned upload layout.
        byte* dst = upload.Map<byte>(0);
        long srcRowBytes = (long)rowSizes[0];
        int rows = (int)rowCounts[0];
        long dstRowPitch = fp.Footprint.RowPitch;
        fixed (byte* srcBase = data.Pixels) {
            for (int r = 0; r < rows; r++)
                System.Buffer.MemoryCopy(
                    srcBase + r * srcRowBytes, dst + (long)fp.Offset + r * dstRowPitch,
                    srcRowBytes, srcRowBytes);
        }
        upload.Unmap(0);

        // The copy runs on the DEDICATED upload command list (separate from the render path's list).
        // Sharing one command list between BeginRender and interleaved asset uploads was corrupting both
        // (texture CopyTextureRegion E_FAILed). See Dx12Device.ExecuteUpload.
        Dx12Backend.Device.ExecuteUpload(cl => {
            var d = new TextureCopyLocation(resource, 0);
            var s = new TextureCopyLocation(upload, fp);
            cl.CopyTextureRegion(d, 0, 0, 0, s, null);
            cl.ResourceBarrierTransition(resource, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
        });

        srvIndex = Dx12Backend.SrvStore.Allocate();
        var srvDesc = new ShaderResourceViewDescription {
            Format = format,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
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
