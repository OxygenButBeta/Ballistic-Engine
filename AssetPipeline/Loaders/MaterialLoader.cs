namespace BallisticEngine.AssetPipeline.Loaders;

public static class MaterialLoader {
    public static Material Load(BallisticProject project, string assetPath) {
        var definition = ContentText.ReadJson<MaterialDefinition>(project, assetPath);
        if (definition is null) {
            Debugging.LogError($"'{assetPath}': material definition not found.");
            return null;
        }

        var shader = AssetDatabase.LoadRef<StandardShader>(definition.Shader);
        if (shader is null) {
            Debugging.LogError($"'{assetPath}': shader '{definition.Shader}' failed to load; material unusable.");
            return null;
        }

        Material material = Material.Create(
            shader, Slot(definition, TextureType.Diffuse, assetPath) ?? FallbackAssets.PlainDiffuse(),
            Slot(definition, TextureType.Normal, assetPath),
            Slot(definition, TextureType.Metallic, assetPath),
            Slot(definition, TextureType.Roughness, assetPath),
            Slot(definition, TextureType.AO, assetPath),
            Slot(definition, TextureType.Emissive, assetPath));

        ApplyScalars(material, definition);
        ApplyCustomProperties(material, definition);
        return material;
    }

    public static void ApplyCustomProperties(Material material, MaterialDefinition definition) {
        var props = material.Shader?.Properties;
        if (props is null) return;
        foreach (var p in props) {
            if (p.Semantic != MaterialSemantic.None) continue;
            switch (p.Type) {
                case ShaderPropertyType.Texture2D: {
                    string reference = definition.CustomTextures is not null &&
                        definition.CustomTextures.TryGetValue(p.Name, out var r) ? r : p.DefaultTexture;
                    var tex = reference is not null ? AssetDatabase.LoadRef<Texture2D>(reference) : null;
                    material.SetCustom(p.Name, tex);
                    break;
                }
                case ShaderPropertyType.Color:
                case ShaderPropertyType.Vector: {
                    Vector4 v = definition.CustomVectors is not null &&
                        definition.CustomVectors.TryGetValue(p.Name, out var arr) && arr is { Length: >= 1 }
                        ? new Vector4(arr[0], arr.Length > 1 ? arr[1] : 0f, arr.Length > 2 ? arr[2] : 0f,
                                      arr.Length > 3 ? arr[3] : (p.Type == ShaderPropertyType.Color ? 1f : 0f))
                        : p.DefaultVector;
                    material.SetCustom(p.Name, v);
                    break;
                }
                default: {
                    float f = definition.CustomFloats is not null &&
                              definition.CustomFloats.TryGetValue(p.Name, out var val) ? val : p.DefaultFloat;
                    material.SetCustom(p.Name, f);
                    break;
                }
            }
        }
    }

    public static void ApplyScalars(Material material, MaterialDefinition definition) {
        material.Transparent = definition.Transparent;
        material.Opacity = Math.Clamp(definition.Opacity, 0f, 1f);
        material.EmissiveIntensity = MathF.Max(definition.EmissiveIntensity, 0f);
        bool authoredEmissiveColor = false;
        if (definition.EmissiveColor is { Length: >= 3 } emissive) {
            var c = new Vector3(emissive[0], emissive[1], emissive[2]);
            material.EmissiveColor = c;
            authoredEmissiveColor = c.LengthSquared() > 1e-6f;
        }

        material.IsEmissive = material.Emissive is not null ||
                              (authoredEmissiveColor && material.EmissiveIntensity > 0f);
        material.PackedOrm = ResolvePackedOrm(definition);
        material.Cutout = ResolveCutout(definition);

        material.BaseColorFactor = definition.BaseColor switch {
            { Length: >= 4 } c => new Vector4(c[0], c[1], c[2], c[3]),
            { Length: 3 } c => new Vector4(c[0], c[1], c[2], 1f),
            _ => Vector4.One,
        };
        material.MetallicFactor = Math.Clamp(
            definition.Metallic ?? (material.Metallic is not null ? 1f : 0f), 0f, 1f);
        material.RoughnessFactor = Math.Clamp(definition.Roughness ?? 1f, 0f, 1f);
        material.NormalStrength = MathF.Max(definition.NormalStrength ?? 1f, 0f);
        material.NormalFlipY = definition.NormalFlipY ?? true;

        material.SyncBagFromTypedFields();
    }

    public static bool ResolvePackedOrm(MaterialDefinition definition) {
        if (definition.PackedOrm is { } explicitValue)
            return explicitValue;

        if (definition.Textures is null ||
            !definition.Textures.TryGetValue(TextureType.Metallic.ToString(), out var reference) ||
            reference is null)
            return false;

        var path = AssetRef.IsGuidRef(reference, out Guid guid) ? AssetDatabase.GuidToAssetPath(guid) : reference;
        return path is not null &&
               Path.GetFileNameWithoutExtension(path).Contains("spec", StringComparison.OrdinalIgnoreCase);
    }

    static readonly string[] CutoutNameHints =
        ["foliage", "leaf", "leaves", "ivy", "plant", "branch", "grass", "vine", "hedge",
         "vegetation", "bush", "boxwood", "flower"];

    public static bool ResolveCutout(MaterialDefinition definition) {
        if (definition.Cutout is { } explicitValue)
            return explicitValue;

        if (definition.Textures is null ||
            !definition.Textures.TryGetValue(TextureType.Diffuse.ToString(), out var reference) ||
            reference is null)
            return false;

        var path = AssetRef.IsGuidRef(reference, out Guid guid) ? AssetDatabase.GuidToAssetPath(guid) : reference;
        if (path is null)
            return false;

        var name = Path.GetFileNameWithoutExtension(path);
        foreach (var hint in CutoutNameHints)
            if (name.Contains(hint, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    static Texture2D Slot(MaterialDefinition definition, TextureType slot, string assetPath) {
        if (definition.Textures is null || !definition.Textures.TryGetValue(slot.ToString(), out var reference))
            return null;

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
