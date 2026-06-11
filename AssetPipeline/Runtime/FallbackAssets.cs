namespace BallisticEngine.AssetPipeline;

// Procedural 4x4 stand-in textures used when a material references a texture that
// is missing or fails to load. Diffuse is magenta so the problem is visible on screen.
public static class FallbackAssets {
    static readonly Dictionary<TextureType, Texture2D> cache = new();
    static Texture2D plainWhiteDiffuse;

    // For materials that intentionally have no diffuse map (e.g. untextured source materials):
    // plain white instead of the magenta error texture.
    public static Texture2D PlainDiffuse() {
        if (plainWhiteDiffuse is not null)
            return plainWhiteDiffuse;

        const int size = 4;
        var pixels = new byte[size * size * 4];
        Array.Fill(pixels, (byte)255);

        TextureData data = new(size, size, TextureFormat.RGBA8, pixels);
        plainWhiteDiffuse = GraphicAPI.CreateTexture2D(in data, TextureType.Diffuse);
        return plainWhiteDiffuse;
    }

    public static Texture2D For(TextureType type) {
        if (cache.TryGetValue(type, out Texture2D existing))
            return existing;

        (byte r, byte g, byte b) = type switch {
            TextureType.Diffuse => ((byte)255, (byte)0, (byte)255),
            TextureType.Normal => ((byte)128, (byte)128, (byte)255), // flat +Z normal
            TextureType.Emissive => ((byte)0, (byte)0, (byte)0),     // broken emissive must not glow
            _ => ((byte)255, (byte)255, (byte)255),
        };

        const int size = 4;
        var pixels = new byte[size * size * 4];
        for (var i = 0; i < pixels.Length; i += 4) {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 255;
        }

        TextureData data = new(size, size, TextureFormat.RGBA8, pixels);
        Texture2D texture = GraphicAPI.CreateTexture2D(in data, type);
        cache[type] = texture;
        return texture;
    }
}
