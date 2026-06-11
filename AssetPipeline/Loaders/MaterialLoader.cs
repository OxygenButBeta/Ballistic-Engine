namespace BallisticEngine.AssetPipeline.Loaders;

// Missing/broken texture refs fall back to flat stand-ins; a missing/broken shader makes the
// whole material unloadable (null). The .mat shape itself lives in MaterialDefinition.cs.
public static class MaterialLoader {
    public static Material Load(BallisticProject project, string assetPath) {
        var definition = PipelineJson.Read<MaterialDefinition>(project.ResolveAbsolute(assetPath));

        var shader = AssetDatabase.LoadRef<StandardShader>(definition.Shader);
        if (shader is null) {
            Debugging.LogError($"'{assetPath}': shader '{definition.Shader}' failed to load; material unusable.");
            return null;
        }

        Material material = Material.Create(
            shader,
            // Unassigned diffuse renders plain white; the magenta error texture is reserved for
            // refs that exist but fail to load (inside Slot).
            Slot(definition, TextureType.Diffuse, assetPath) ?? FallbackAssets.PlainDiffuse(),
            Slot(definition, TextureType.Normal, assetPath),
            Slot(definition, TextureType.Metallic, assetPath),
            Slot(definition, TextureType.Roughness, assetPath),
            Slot(definition, TextureType.AO, assetPath),
            Slot(definition, TextureType.Emissive, assetPath));

        ApplyScalars(material, definition);
        return material;
    }

    public static void ApplyScalars(Material material, MaterialDefinition definition) {
        material.Transparent = definition.Transparent;
        material.Opacity = Math.Clamp(definition.Opacity, 0f, 1f);
        material.EmissiveIntensity = MathF.Max(definition.EmissiveIntensity, 0f);
        if (definition.EmissiveColor is { Length: >= 3 } emissive)
            material.EmissiveColor = new OpenTK.Mathematics.Vector3(emissive[0], emissive[1], emissive[2]);
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
