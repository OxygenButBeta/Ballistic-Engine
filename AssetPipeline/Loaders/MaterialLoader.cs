namespace BallisticEngine.AssetPipeline.Loaders;

// .mat asset: { "version": 1, "shader": "<.shader ref>", "textures": { "Diffuse": "<ref>", ... } }
// Texture keys are TextureType names. Missing/broken texture refs fall back to flat stand-ins;
// a missing/broken shader makes the whole material unloadable (null).
public sealed class MaterialDefinition {
    public int Version { get; set; } = 1;
    public string Shader { get; set; }
    public Dictionary<string, string> Textures { get; set; } = new();
}

public static class MaterialLoader {
    public static Material Load(BallisticProject project, string assetPath) {
        var definition = PipelineJson.Read<MaterialDefinition>(project.ResolveAbsolute(assetPath));

        var shader = AssetDatabase.LoadRef<StandardShader>(definition.Shader);
        if (shader is null) {
            Debugging.LogError($"'{assetPath}': shader '{definition.Shader}' failed to load; material unusable.");
            return null;
        }

        return Material.Create(
            shader,
            Slot(definition, TextureType.Diffuse, assetPath) ?? FallbackAssets.For(TextureType.Diffuse),
            Slot(definition, TextureType.Normal, assetPath),
            Slot(definition, TextureType.Metallic, assetPath),
            Slot(definition, TextureType.Roughness, assetPath),
            Slot(definition, TextureType.AO, assetPath));
    }

    static Texture2D Slot(MaterialDefinition definition, TextureType slot, string assetPath) {
        if (definition.Textures is null || !definition.Textures.TryGetValue(slot.ToString(), out var reference))
            return null; // slot intentionally unassigned

        Texture2D texture = AssetDatabase.LoadRef<Texture2D>(reference);
        if (texture is null) {
            Debugging.LogWarning($"'{assetPath}': {slot} texture '{reference}' failed to load; using fallback.");
            return FallbackAssets.For(slot);
        }

        if (texture.Type != slot)
            Debugging.LogWarning(
                $"'{assetPath}': {slot} slot uses '{reference}', which is imported as {texture.Type}. " +
                "It will bind to the wrong sampler; fix its .meta textureType.");

        return texture;
    }
}
