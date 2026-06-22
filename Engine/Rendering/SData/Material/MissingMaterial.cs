namespace BallisticEngine;

public static class MissingMaterial {
    const string StandardShaderPath = "Assets/Default/Shaders/Standard.shader";

    static Material instance;

    public static Material Get() {
        if (instance is not null)
            return instance;

        var shader = AssetDatabase.LoadRef<StandardShader>(StandardShaderPath);
        if (shader is null)
            return null;

        instance = Material.Create(shader, Checker());
        instance.Name = "Missing (no material)";
        return instance;
    }

    static Texture2D Checker() {
        const int size = 8, cell = 1;
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++) {
                bool on = ((x / cell) + (y / cell)) % 2 == 0;
                int i = (y * size + x) * 4;
                pixels[i] = on ? (byte)255 : (byte)0;
                pixels[i + 1] = 0;
                pixels[i + 2] = on ? (byte)255 : (byte)0;
                pixels[i + 3] = 255;
            }
        TextureData data = new(size, size, TextureFormat.RGBA8, pixels);
        return GraphicAPI.CreateTexture2D(in data, TextureType.Diffuse);
    }
}
