using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

public sealed class Dx12Texture2D : Texture2D {
    public override int UID { get; protected set; }
    static int nextId = 1;

    ID3D12Resource resource;
    int srvIndex = -1;
    public ID3D12Resource Resource => resource;

    public bool HasSrv => srvIndex >= 0;
    public CpuDescriptorHandle SrvCpu => Dx12Backend.SrvStore.Cpu(srvIndex);

    public Dx12Texture2D() {
        UID = nextId++;
    }

    public void UploadPublic(in TextureData data, TextureType type) => Upload(in data, type);

    protected override unsafe void Upload(in TextureData data, TextureType type) {
        TextureData d = data;
        Dx12Backend.Device.RunExclusive(() => UploadCore(in d, type));
    }

    unsafe void UploadCore(in TextureData data0, TextureType type) {
        Type = type;
        if (!data0.IsValid)
            return;

        TextureData data = (data0.MipCount <= 1 && data0.Format == TextureFormat.RGBA8
                                                && data0.Width >= 2 && data0.Height >= 2)
            ? GenerateRgba8Mips(in data0) : data0;

        Format format = Dx12Backend.ToDxgi(data.Format, type);
        int mipCount = Math.Max(1, data.MipCount);

        var desc = ResourceDescription.Texture2D(format, (uint)data.Width, (uint)data.Height,
            arraySize: 1, mipLevels: (ushort)mipCount);
        resource = Dx12Backend.Device.Device.CreateCommittedResource(
            HeapProperties.DefaultHeapProperties, HeapFlags.None, desc, ResourceStates.CopyDest);
        resource.Name = $"Tex2D#{UID}({type})";

        var footprints = new PlacedSubresourceFootPrint[mipCount];
        var rowCounts = new uint[mipCount];
        var rowSizes = new ulong[mipCount];
        Dx12Backend.Device.Device.GetCopyableFootprints(desc, 0, (uint)mipCount, 0,
            footprints, rowCounts, rowSizes, out ulong totalBytes);

        using ID3D12Resource upload = Dx12Backend.Device.Device.CreateCommittedResource(
            HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(totalBytes), ResourceStates.GenericRead);

        byte* dst = upload.Map<byte>(0);
        fixed (byte* srcBase = data.Pixels) {
            for (int mip = 0; mip < mipCount; mip++) {
                PlacedSubresourceFootPrint fp = footprints[mip];
                long srcOffset = TextureMipLayout.LevelOffset(data.Width, data.Height, mip, data.Format);
                long srcRowBytes = (long)rowSizes[mip];
                int rows = (int)rowCounts[mip];
                long dstRowPitch = fp.Footprint.RowPitch;
                byte* dstMip = dst + (long)fp.Offset;
                byte* srcMip = srcBase + srcOffset;
                for (int r = 0; r < rows; r++)
                    System.Buffer.MemoryCopy(srcMip + r * srcRowBytes, dstMip + r * dstRowPitch,
                        srcRowBytes, srcRowBytes);
            }
        }
        upload.Unmap(0);

        Dx12Backend.Device.ExecuteUpload(cl => {
            for (int mip = 0; mip < mipCount; mip++) {
                var d = new TextureCopyLocation(resource, (uint)mip);
                var s = new TextureCopyLocation(upload, footprints[mip]);
                cl.CopyTextureRegion(d, 0, 0, 0, s, null);
            }
            cl.ResourceBarrierTransition(resource, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
        });

        srvIndex = Dx12Backend.SrvStore.Allocate();
        var srvDesc = new ShaderResourceViewDescription {
            Format = format,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = (uint)mipCount, MostDetailedMip = 0 },
        };
        Dx12Backend.Device.Device.CreateShaderResourceView(resource, srvDesc, Dx12Backend.SrvStore.Cpu(srvIndex));
    }

    static TextureData GenerateRgba8Mips(in TextureData src) {
        int levels = 1;
        for (int w = src.Width, h = src.Height; w > 1 || h > 1; w = Math.Max(1, w >> 1), h = Math.Max(1, h >> 1))
            levels++;
        long total = TextureMipLayout.ChainBytes(src.Width, src.Height, levels, TextureFormat.RGBA8);
        var chain = new byte[total];
        Array.Copy(src.Pixels, chain, Math.Min(src.Pixels.Length, (long)src.Width * src.Height * 4));

        for (int level = 1; level < levels; level++) {
            var (dw, dh) = TextureMipLayout.LevelSize(src.Width, src.Height, level);
            var (sw, sh) = TextureMipLayout.LevelSize(src.Width, src.Height, level - 1);
            long srcOff = TextureMipLayout.LevelOffset(src.Width, src.Height, level - 1, TextureFormat.RGBA8);
            long dstOff = TextureMipLayout.LevelOffset(src.Width, src.Height, level, TextureFormat.RGBA8);
            for (int y = 0; y < dh; y++) {
                int sy0 = Math.Min(y * 2, sh - 1), sy1 = Math.Min(y * 2 + 1, sh - 1);
                for (int x = 0; x < dw; x++) {
                    int sx0 = Math.Min(x * 2, sw - 1), sx1 = Math.Min(x * 2 + 1, sw - 1);
                    long p00 = srcOff + ((long)sy0 * sw + sx0) * 4, p10 = srcOff + ((long)sy0 * sw + sx1) * 4;
                    long p01 = srcOff + ((long)sy1 * sw + sx0) * 4, p11 = srcOff + ((long)sy1 * sw + sx1) * 4;
                    long d = dstOff + ((long)y * dw + x) * 4;
                    for (int c = 0; c < 4; c++)
                        chain[d + c] = (byte)((chain[p00 + c] + chain[p10 + c] + chain[p01 + c] + chain[p11 + c] + 2) / 4);
                }
            }
        }
        return new TextureData(src.Width, src.Height, TextureFormat.RGBA8, chain, levels);
    }

    public override void Activate() {
    }
    public override void Deactivate() { }

    public override void Dispose() {
        resource?.Dispose();
        resource = null;
    }
}
