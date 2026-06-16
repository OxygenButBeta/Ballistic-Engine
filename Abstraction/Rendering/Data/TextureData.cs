namespace BallisticEngine;

// CPU-side decoded image. Carries no GPU state, so it can be produced off the GL thread.
//
// For block-compressed formats (BC1/BC3/BC5), Pixels holds the full mip chain concatenated
// largest-first and MipCount says how many levels are present; each level's dimensions and byte
// length are derived from Width/Height/Format (see TextureMipLayout). Uncompressed RGBA8/RGBA32F
// arrives single-level (MipCount 1); the backend builds the mip chain at upload time (the DX12
// path does this in Dx12Texture2D — the old GL GenerateMipmap equivalent, restored for D3).
public readonly struct TextureData {
    public readonly int Width;
    public readonly int Height;
    public readonly TextureFormat Format;
    public readonly byte[] Pixels;
    public readonly int MipCount;

    public TextureData(int width, int height, TextureFormat format, byte[] pixels, int mipCount = 1) {
        Width = width;
        Height = height;
        Format = format;
        Pixels = pixels;
        MipCount = mipCount < 1 ? 1 : mipCount;
    }

    public bool IsValid => Pixels is not null && Width > 0 && Height > 0;
}

// Computes the byte layout of a (possibly mipped, possibly block-compressed) texture. Shared by the
// artifact writer/reader and the GL upload so they agree on offsets without storing a table.
public static class TextureMipLayout {
    // Dimensions of mip `level` (0 = base), floored to a minimum of 1.
    public static (int w, int h) LevelSize(int width, int height, int level) {
        int w = Math.Max(1, width >> level);
        int h = Math.Max(1, height >> level);
        return (w, h);
    }

    // Byte length of one mip level for the given format.
    public static long LevelBytes(int width, int height, int level, TextureFormat format) {
        var (w, h) = LevelSize(width, height, level);
        if (format.IsBlockCompressed()) {
            long blocksWide = Math.Max(1, (w + 3) / 4);
            long blocksHigh = Math.Max(1, (h + 3) / 4);
            return blocksWide * blocksHigh * format.BlockBytes();
        }
        int bpp = format == TextureFormat.RGBA32F ? 16 : 4;
        return (long)w * h * bpp;
    }

    // Total bytes for `mipCount` levels starting at the base size.
    public static long ChainBytes(int width, int height, int mipCount, TextureFormat format) {
        long total = 0;
        for (int level = 0; level < mipCount; level++)
            total += LevelBytes(width, height, level, format);
        return total;
    }

    // Byte offset of mip `level` within a chain laid out largest-first.
    public static long LevelOffset(int width, int height, int level, TextureFormat format) {
        long offset = 0;
        for (int l = 0; l < level; l++)
            offset += LevelBytes(width, height, l, format);
        return offset;
    }
}
