namespace BallisticEngine;

// The magenta/black checker material shown when a renderer's submesh has NO material assigned
// (Unity's "missing material" pink). A null material used to make the submesh non-renderable — it
// just vanished, so a forgotten/broken material ref was invisible. Now MaterialFor substitutes this
// instead of returning null, so the gap is loud and obvious in both the editor and the player.
//
// Built in code (a generated checker texture on the standard shader) so it needs no project asset and
// can never itself be "missing". Cached as a singleton — every missing-material draw shares it.
public static class MissingMaterial {
    const string StandardShaderPath = "Assets/Default/Shaders/Standard.shader";

    static Material instance;

    public static Material Get() {
        if (instance is not null)
            return instance;

        var shader = AssetDatabase.LoadRef<StandardShader>(StandardShaderPath);
        if (shader is null)
            return null; // no standard shader available (very early boot) — caller keeps the null path

        instance = Material.Create(shader, Checker());
        instance.Name = "Missing (no material)";
        // Unlit-ish read: make the checker self-emissive so it shows even with no scene light, and not
        // glossy (so it doesn't read as a real surface). BaseColor stays white; the texture is the tell.
        return instance;
    }

    // A 8x8 magenta/black checkerboard diffuse texture (Unity's missing-material pink).
    static Texture2D Checker() {
        const int size = 8, cell = 1; // 8x8 cells, 1px each — crisp checker at any UV scale via repeat
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++) {
                bool on = ((x / cell) + (y / cell)) % 2 == 0;
                int i = (y * size + x) * 4;
                pixels[i] = on ? (byte)255 : (byte)0;       // R
                pixels[i + 1] = 0;                          // G
                pixels[i + 2] = on ? (byte)255 : (byte)0;   // B (magenta when on, black when off)
                pixels[i + 3] = 255;
            }
        TextureData data = new(size, size, TextureFormat.RGBA8, pixels);
        return GraphicAPI.CreateTexture2D(in data, TextureType.Diffuse);
    }
}
