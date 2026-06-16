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
    // True once a valid persistent SRV exists (Upload succeeded). False for a texture whose data was invalid
    // — sampling SrvCpu when -1 yields a descriptor before the heap start (GPU device-removal). Callers that
    // copy SrvCpu into a shader-visible table MUST check this and substitute a fallback when false.
    public bool HasSrv => srvIndex >= 0;
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

    unsafe void UploadCore(in TextureData data0, TextureType type) {
        Type = type;
        if (!data0.IsValid)
            return;

        // V2 (fixes D3 — normal-map aliasing sparkle): the GL path called GenerateMipmap on upload; the DX12 port
        // never did, so a single-level RGBA8 texture (most Bistro maps fall through the importer's no-BC path with
        // MipCount=1) reached the GPU with ONE mip → no filtering → normal maps aliased into a crawling speckle
        // (and color/roughness shimmered). Build the box-filtered mip chain here for uncompressed RGBA8 so the
        // sampler's LOD selection (and the G-buffer NormalLodBias) actually has coarser levels to fetch. BC
        // textures already carry a baked chain from CompressWithMips; RGBA32F (HDR env) stays single-level.
        TextureData data = (data0.MipCount <= 1 && data0.Format == TextureFormat.RGBA8
                            && data0.Width >= 2 && data0.Height >= 2)
            ? GenerateRgba8Mips(in data0) : data0;

        Format format = Dx12Backend.ToDxgi(data.Format, type);
        int mipCount = Math.Max(1, data.MipCount);   // pre-baked chain (BC), generated above (RGBA8), or 1

        var desc = ResourceDescription.Texture2D(format, (uint)data.Width, (uint)data.Height,
            arraySize: 1, mipLevels: (ushort)mipCount);
        resource = Dx12Backend.Device.Device.CreateCommittedResource(
            HeapProperties.DefaultHeapProperties, HeapFlags.None, desc, ResourceStates.CopyDest);
        resource.Name = $"Tex2D#{UID}({type})";

        // Footprints for every mip — DX12 demands 256-byte-aligned upload row pitches per subresource.
        var footprints = new PlacedSubresourceFootPrint[mipCount];
        var rowCounts = new uint[mipCount];
        var rowSizes = new ulong[mipCount];
        Dx12Backend.Device.Device.GetCopyableFootprints(desc, 0, (uint)mipCount, 0,
            footprints, rowCounts, rowSizes, out ulong totalBytes);

        using ID3D12Resource upload = Dx12Backend.Device.Device.CreateCommittedResource(
            HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(totalBytes), ResourceStates.GenericRead);

        // Copy each mip's tightly-packed source rows (block-rows for BC) into the aligned upload layout.
        // The source chain is largest-first (TextureMipLayout); use rowSizes/rowCounts from D3D so BC
        // sub-4×4 mips copy the right block-row count. Now safe under the dedicated upload queue (the
        // earlier multi-mip E_FAIL was the SHARED command list, not this math).
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

        // The copy runs on the DEDICATED upload command list (separate from the render path's list) —
        // sharing one list between BeginRender and interleaved uploads corrupted both. See ExecuteUpload.
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

    // Build a box-filtered RGBA8 mip chain (largest-first, concatenated) from a single-level RGBA8 image, so
    // single-mip material textures get proper LOD filtering on the GPU (V2 D3 fix). Mirrors the importer's
    // CompressWithMips downsample, but uncompressed and at upload time (fixes already-imported content with no
    // re-import). sRGB-correctness is approximated by a straight average — the existing importer mip path does
    // the same; a fully correct linear-space average is a follow-up if banding shows.
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

    public override void Activate() { /* DX12 binds by descriptor table at draw time */ }
    public override void Deactivate() { }

    public override void Dispose() {
        resource?.Dispose();
        resource = null;
    }
}
