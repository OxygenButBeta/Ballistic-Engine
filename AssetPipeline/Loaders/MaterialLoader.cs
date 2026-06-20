namespace BallisticEngine.AssetPipeline.Loaders;

// Missing/broken texture refs fall back to flat stand-ins; a missing/broken shader makes the
// whole material unloadable (null). The .mat shape itself lives in MaterialDefinition.cs.
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
        ApplyCustomProperties(material, definition);
        return material;
    }

    // Resolve the shader's CUSTOM (semantic == None) declared properties into the material's name-keyed
    // custom bag: the .mat's custom value if stated, otherwise the property's DECLARED DEFAULT (the loader
    // is the default authority here, mirroring ApplyScalars for Standard props). A Standard shader declares
    // no None props → this is a no-op and the custom bag stays empty (byte-identical .mat round-trip).
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
                default: { // Float / Range
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
        // Emissive when there's a map OR an authored non-black colour with positive intensity. The
        // renderer's HasEmissive gates on this, so a COLOR-ONLY emissive emits (not just textured).
        material.IsEmissive = material.Emissive is not null ||
                              (authoredEmissiveColor && material.EmissiveIntensity > 0f);
        material.PackedOrm = ResolvePackedOrm(definition);
        material.Cutout = ResolveCutout(definition);

        // Scalar PBR factors (glTF semantics). Unstated metallic defaults to 1 with a metallic
        // map (the map drives it) and 0 without one (untextured = dielectric, not chrome).
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

        // Project the fully-resolved fields into the shader-declared property bag — the renderer reads
        // the bag now (Stage 3), so EVERY path that resolves a material (load + the editor's live edit)
        // must refresh it here. ApplyScalars is the sole default authority (incl. the metallic-map
        // conditional above), so deriving the bag from its output keeps the two from ever drifting.
        // (Texture slots are set by the caller BEFORE this runs, so they're already current.)
        material.SyncBagFromTypedFields();
    }

    // Falcor/glTF-style "Specular" maps pack (occlusion, roughness, metallic) into RGB; reading
    // them as a grayscale metallic mask renders everything as black metal. When the .mat doesn't
    // say explicitly, infer from the metallic texture's file name.
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

    // Foliage-style assets keep the leaf silhouette in the diffuse alpha channel; without
    // cutout they render as full white quads. Infer from the texture name unless the .mat
    // says explicitly.
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
