namespace BallisticEngine;

// Supplies a lit material for terrain that has none assigned. A generated terrain mesh carries no
// baked submesh material refs (unlike imported models), so without this the renderer treats it as
// non-renderable (Renderer.IsRenderable needs a material). Mirrors the editor's Primitives default:
// prefer the project's Default.mat asset, fall back to a code-built grey StandardShader material.
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
