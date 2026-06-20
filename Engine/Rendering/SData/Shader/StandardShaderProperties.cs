namespace BallisticEngine;

// The canonical property declaration for the built-in Standard (PBR) shader — the single source of
// truth that ties StandardOpaque.hlsl's fixed inputs to authorable properties.
//
// CRITICAL (byte-identity): every default below MUST equal the corresponding field default on
// `new Material()`, because a material that overrides NOTHING must shade exactly as it does today.
// MaterialDefaultsMatchDeclaration() asserts this so the two can't drift; it's exercised by the
// material round-trip test / a CLI check.
//
// Texture order matters: the renderer binds texture properties to SRV slots t0..t5 in DECLARED
// order, which must be Diffuse, Normal, Metallic, Roughness, AO, Emissive (the current fixed order).
public static class StandardShaderProperties {
    static ShaderProperties cached;

    public static ShaderProperties Build() => cached ??= new ShaderProperties([
        // --- Texture slots (t0..t5, declared order is the bind order) ---
        ShaderProperty.Texture("_Diffuse", "Albedo", MaterialSemantic.DiffuseMap),
        ShaderProperty.Texture("_Normal", "Normal Map", MaterialSemantic.NormalMap),
        ShaderProperty.Texture("_Metallic", "Metallic", MaterialSemantic.MetallicMap),
        ShaderProperty.Texture("_Roughness", "Roughness", MaterialSemantic.RoughnessMap),
        ShaderProperty.Texture("_AO", "Ambient Occlusion", MaterialSemantic.AOMap),
        ShaderProperty.Texture("_Emissive", "Emission Map", MaterialSemantic.EmissiveMap),

        // --- Base PBR factors ---
        ShaderProperty.ColorProp("_BaseColor", "Base Color", MaterialSemantic.BaseColorFactor, Vector4.One),
        ShaderProperty.RangeProp("_Metallic_Factor", "Metallic Factor", MaterialSemantic.MetallicFactor, 0f, 0f, 1f),
        ShaderProperty.RangeProp("_Roughness_Factor", "Roughness Factor", MaterialSemantic.RoughnessFactor, 1f, 0f, 1f),
        ShaderProperty.RangeProp("_Specular", "Specular Reflectance", MaterialSemantic.SpecularReflectance, 0.5f, 0f, 1f),

        // --- Normal map controls ---
        ShaderProperty.FloatProp("_NormalStrength", "Normal Strength", MaterialSemantic.NormalStrength, 1f),
        ShaderProperty.FloatProp("_NormalFlipY", "Normal Flip Y", MaterialSemantic.NormalFlipY, 1f), // bool-as-float: 1 = true (default)

        // --- Emission ---
        ShaderProperty.ColorProp("_EmissionColor", "Emission Color", MaterialSemantic.EmissiveColor,
            new Vector4(1f, 1f, 1f, 1f)),
        ShaderProperty.FloatProp("_EmissionIntensity", "Emission Intensity", MaterialSemantic.EmissiveIntensity, 1f),

        // --- Clearcoat ---
        ShaderProperty.RangeProp("_Clearcoat", "Clearcoat", MaterialSemantic.Clearcoat, 0f, 0f, 1f),
        ShaderProperty.RangeProp("_ClearcoatRoughness", "Clearcoat Roughness", MaterialSemantic.ClearcoatRoughness, 0.1f, 0f, 1f),

        // --- Blend / flags ---
        ShaderProperty.FloatProp("_Transparent", "Transparent", MaterialSemantic.Transparent, 0f),       // bool-as-float
        ShaderProperty.RangeProp("_Opacity", "Opacity", MaterialSemantic.Opacity, 1f, 0f, 1f),
        ShaderProperty.FloatProp("_PackedOrm", "ORM-Packed Metallic", MaterialSemantic.PackedOrm, 0f),    // bool-as-float
        ShaderProperty.FloatProp("_Cutout", "Alpha Cutout", MaterialSemantic.Cutout, 0f),                 // bool-as-float
    ]);

    // Guards the byte-identity invariant: declared defaults == new Material() field defaults. Returns
    // null when consistent, otherwise the first mismatch message. Called by tests / a CLI self-check.
    public static string MaterialDefaultsMatchDeclaration() {
        var m = Material.Default();
        var props = Build();
        string Check(MaterialSemantic s, float expected) {
            var p = props.BySemantic(s);
            return p is { DefaultFloat: var d } && Math.Abs(d - expected) < 1e-6f
                ? null : $"{s}: declared {p?.DefaultFloat}, material {expected}";
        }
        string CheckVec(MaterialSemantic s, Vector4 expected) {
            var p = props.BySemantic(s);
            return p is not null && p.DefaultVector == expected
                ? null : $"{s}: declared {p?.DefaultVector}, material {expected}";
        }
        return Check(MaterialSemantic.MetallicFactor, m.MetallicFactor)
            ?? Check(MaterialSemantic.RoughnessFactor, m.RoughnessFactor)
            ?? Check(MaterialSemantic.SpecularReflectance, m.SpecularReflectance)
            ?? Check(MaterialSemantic.NormalStrength, m.NormalStrength)
            ?? Check(MaterialSemantic.Clearcoat, m.Clearcoat)
            ?? Check(MaterialSemantic.ClearcoatRoughness, m.ClearcoatRoughness)
            ?? Check(MaterialSemantic.Opacity, m.Opacity)
            ?? Check(MaterialSemantic.EmissiveIntensity, m.EmissiveIntensity)
            ?? CheckVec(MaterialSemantic.BaseColorFactor, m.BaseColorFactor)
            ?? CheckVec(MaterialSemantic.EmissiveColor, new Vector4(m.EmissiveColor, 1f));
    }
}
