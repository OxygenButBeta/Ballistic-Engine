namespace BallisticEngine;

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

public static class TextureMipLayout {
    public static (int w, int h) LevelSize(int width, int height, int level) {
        int w = Math.Max(1, width >> level);
        int h = Math.Max(1, height >> level);
        return (w, h);
    }

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

    public static long ChainBytes(int width, int height, int mipCount, TextureFormat format) {
        long total = 0;
        for (int level = 0; level < mipCount; level++)
            total += LevelBytes(width, height, level, format);
        return total;
    }

    public static long LevelOffset(int width, int height, int level, TextureFormat format) {
        long offset = 0;
        for (int l = 0; l < level; l++)
            offset += LevelBytes(width, height, l, format);
        return offset;
    }
}
