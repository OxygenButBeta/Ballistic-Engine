namespace BallisticEngine;

public static class DefaultTextures {
    static readonly Dictionary<TextureType, Texture2D> cache = new();

    public static Texture2D Neutral(TextureType type) {
        if (cache.TryGetValue(type, out Texture2D existing))
            return existing;

        (byte r, byte g, byte b) = type switch {
            TextureType.Normal => ((byte)128, (byte)128, (byte)255),
            TextureType.Metallic => ((byte)0, (byte)0, (byte)0),
            TextureType.Emissive => ((byte)255, (byte)255, (byte)255),
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
