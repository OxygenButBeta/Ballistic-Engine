namespace BallisticEngine;

public static class TextureFormatExtensions {
    public static bool IsBlockCompressed(this TextureFormat format) =>
        format is TextureFormat.BC1 or TextureFormat.BC3 or TextureFormat.BC5;

    public static int BlockBytes(this TextureFormat format) => format == TextureFormat.BC1 ? 8 : 16;
}
