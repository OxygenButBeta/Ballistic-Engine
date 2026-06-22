namespace BallisticEngine;

public static class TerrainDefaultMaterial {
    public const string DefaultMaterialPath = "Assets/Default/Materials/Default.mat";
    public const string StandardShaderPath = "Assets/Default/Shaders/Standard.shader";

    static Material fallback;

    public static Material Get() {
        Material asset = AssetDatabase.Load<Material>(DefaultMaterialPath);
        if (asset is not null)
            return asset;

        if (fallback is not null)
            return fallback;

        var shader = AssetDatabase.LoadRef<StandardShader>(StandardShaderPath);
        if (shader is null)
            return null;

        fallback = Material.Create(shader, DefaultTextures.Neutral(TextureType.Diffuse));
        fallback.Name = "Terrain Default";
        return fallback;
    }
}
