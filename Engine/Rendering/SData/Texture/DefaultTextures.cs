namespace BallisticEngine;

// Neutral 1-value stand-ins bound for material slots that have no texture assigned, so
// shaders never sample stale bindings from the previous draw (or undefined unbound units).
// Semantics: metallic 0 (dielectric), roughness 1 (fully rough), AO 1 (unoccluded),
// emissive 0 (dark), flat +Z normal.
public static class DefaultTextures {
    static readonly Dictionary<TextureType, Texture2D> cache = new();

    public static Texture2D Neutral(TextureType type) {
        if (cache.TryGetValue(type, out Texture2D existing))
            return existing;

        (byte r, byte g, byte b) = type switch {
            TextureType.Normal => ((byte)128, (byte)128, (byte)255),
            TextureType.Metallic => ((byte)0, (byte)0, (byte)0),
            // WHITE (not black): a color-only emissive material (Ke, no map — neon, screens, area
            // lights, the Cornell light) binds this default and the shader does
            // texture(Emissive)*EmissiveFactor, so white*factor = the authored color. Gated by
            // HasEmissive, so a material WITHOUT emissive never samples it.
            TextureType.Emissive => ((byte)255, (byte)255, (byte)255),
            _ => ((byte)255, (byte)255, (byte)255), // diffuse / roughness / AO
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
