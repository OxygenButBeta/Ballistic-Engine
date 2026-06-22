namespace BallisticEngine;

public enum TextureFormat : byte {
    RGBA8 = 1,

    RGBA32F = 2,

    BC1 = 3,
    BC3 = 4,
    BC5 = 5,
}

public static class TextureFormatExtensions {
    public static bool IsBlockCompressed(this TextureFormat format) =>
        format is TextureFormat.BC1 or TextureFormat.BC3 or TextureFormat.BC5;

    public static int BlockBytes(this TextureFormat format) => format == TextureFormat.BC1 ? 8 : 16;
}
