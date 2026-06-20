using BallisticEngine.AssetPipeline.Loaders;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor.Inspector.AssetInspectors;

// Joins a shader-declared property (by MaterialSemantic) to its field on the on-disk MaterialDefinition
// (.mat JSON), so the generated material inspector can read/write each declared property WITHOUT a
// hardcoded type-switch. This is the single place the semantic <-> .mat-field mapping lives.
//
// It preserves the .mat's null-means-default ELISION: a value equal to the property default is stored
// as the JSON's "unstated" form (null / White / the bool's natural default) — byte-for-byte the same
// delta the old hand-rolled UI produced, so opening a material and changing nothing never churns the
// file, and the load-time auto-detect heuristics (ResolvePackedOrm/ResolveCutout) keep working.
sealed class MaterialPropertyBinding {
    public string TextureKey { get; private init; }   // MaterialDefinition.Textures key (TextureType name), for Texture2D
    public bool IsBool { get; private init; }          // float property that's really a flag (checkbox)
    public int ColorComponents { get; private init; }  // 3 (RGB) or 4 (RGBA) for Color properties

    Func<MaterialDefinition, float, float> getFloat;
    Action<MaterialDefinition, float, float> setFloat;
    Func<MaterialDefinition, SysVec4, SysVec4> getVector;
    Action<MaterialDefinition, SysVec4, SysVec4> setVector;

    public float GetFloat(MaterialDefinition d, float dflt) => getFloat(d, dflt);
    public void SetFloat(MaterialDefinition d, float v, float dflt) => setFloat(d, v, dflt);
    public SysVec4 GetVector(MaterialDefinition d, SysVec4 dflt) => getVector(d, dflt);
    public void SetVector(MaterialDefinition d, SysVec4 v, SysVec4 dflt) => setVector(d, v, dflt);

    // Returns the binding for an authorable semantic, or null when the channel isn't directly authored
    // (IsEmissive is load-derived from the emissive map/colour, so it has no inspector row).
    public static MaterialPropertyBinding For(MaterialSemantic semantic) => semantic switch {
        MaterialSemantic.DiffuseMap => Tex(TextureType.Diffuse),
        MaterialSemantic.NormalMap => Tex(TextureType.Normal),
        MaterialSemantic.MetallicMap => Tex(TextureType.Metallic),
        MaterialSemantic.RoughnessMap => Tex(TextureType.Roughness),
        MaterialSemantic.AOMap => Tex(TextureType.AO),
        MaterialSemantic.EmissiveMap => Tex(TextureType.Emissive),

        MaterialSemantic.BaseColorFactor => new MaterialPropertyBinding {
            ColorComponents = 4,
            // White (the property default) stores as null = unstated, exactly like the old UI.
            getVector = (d, _) => d.BaseColor switch {
                { Length: >= 4 } c => new SysVec4(c[0], c[1], c[2], c[3]),
                { Length: 3 } c => new SysVec4(c[0], c[1], c[2], 1f),
                _ => SysVec4.One,
            },
            setVector = (d, v, _) => d.BaseColor = v == SysVec4.One ? null : [v.X, v.Y, v.Z, v.W],
        },
        MaterialSemantic.EmissiveColor => new MaterialPropertyBinding {
            ColorComponents = 3,
            getVector = (d, _) => d.EmissiveColor is { Length: >= 3 } c ? new SysVec4(c[0], c[1], c[2], 1f) : SysVec4.One,
            setVector = (d, v, _) => d.EmissiveColor = [v.X, v.Y, v.Z],
        },

        MaterialSemantic.MetallicFactor => Scalar(
            (d, _) => d.Metallic ?? 0f,           // shown value (raw .mat; load applies the map conditional)
            (d, v, dflt) => d.Metallic = v == dflt ? null : v),
        MaterialSemantic.RoughnessFactor => Scalar(
            (d, dflt) => d.Roughness ?? dflt,
            (d, v, dflt) => d.Roughness = v == dflt ? null : v),
        MaterialSemantic.NormalStrength => Scalar(
            (d, dflt) => d.NormalStrength ?? dflt,
            (d, v, dflt) => d.NormalStrength = v == dflt ? null : v),
        MaterialSemantic.Opacity => Scalar(
            (d, _) => d.Opacity,
            (d, v, _) => d.Opacity = v),

        // Specular / Clearcoat have NO MaterialDefinition field yet — they'd need .mat-schema additions
        // (a future stage). Skipped for now so the inspector doesn't pretend to persist them.
        MaterialSemantic.SpecularReflectance => null,
        MaterialSemantic.Clearcoat => null,
        MaterialSemantic.ClearcoatRoughness => null,
        MaterialSemantic.EmissiveIntensity => Scalar(
            (d, _) => d.EmissiveIntensity,
            (d, v, _) => d.EmissiveIntensity = v),

        MaterialSemantic.NormalFlipY => Flag(
            d => (d.NormalFlipY ?? true) ? 1f : 0f,
            (d, v) => d.NormalFlipY = v != 0f),
        MaterialSemantic.Transparent => Flag(
            d => d.Transparent ? 1f : 0f,
            (d, v) => d.Transparent = v != 0f),
        MaterialSemantic.PackedOrm => Flag(
            d => MaterialLoader.ResolvePackedOrm(d) ? 1f : 0f,    // honour the "spec" filename auto-detect
            (d, v) => d.PackedOrm = v != 0f),
        MaterialSemantic.Cutout => Flag(
            d => MaterialLoader.ResolveCutout(d) ? 1f : 0f,       // honour the foliage-name auto-detect
            (d, v) => d.Cutout = v != 0f),

        _ => null,   // IsEmissive and anything custom (None) — not an inspector row
    };

    static MaterialPropertyBinding Tex(TextureType t) => new() { TextureKey = t.ToString() };

    static MaterialPropertyBinding Scalar(Func<MaterialDefinition, float, float> get,
        Action<MaterialDefinition, float, float> set) =>
        new() { getFloat = get, setFloat = set };

    static MaterialPropertyBinding Flag(Func<MaterialDefinition, float> get,
        Action<MaterialDefinition, float> set) =>
        new() { IsBool = true, getFloat = (d, _) => get(d), setFloat = (d, v, _) => set(d, v) };
}
