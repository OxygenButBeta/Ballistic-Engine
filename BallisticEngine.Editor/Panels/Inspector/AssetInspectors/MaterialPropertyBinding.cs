using BallisticEngine.AssetPipeline.Loaders;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor.Inspector.AssetInspectors;

sealed class MaterialPropertyBinding {
    public string TextureKey { get; private init; }
    public bool IsBool { get; private init; }
    public int ColorComponents { get; private init; }

    Func<MaterialDefinition, float, float> getFloat;
    Action<MaterialDefinition, float, float> setFloat;
    Func<MaterialDefinition, SysVec4, SysVec4> getVector;
    Action<MaterialDefinition, SysVec4, SysVec4> setVector;

    public float GetFloat(MaterialDefinition d, float dflt) => getFloat(d, dflt);
    public void SetFloat(MaterialDefinition d, float v, float dflt) => setFloat(d, v, dflt);
    public SysVec4 GetVector(MaterialDefinition d, SysVec4 dflt) => getVector(d, dflt);
    public void SetVector(MaterialDefinition d, SysVec4 v, SysVec4 dflt) => setVector(d, v, dflt);

    public static MaterialPropertyBinding For(MaterialSemantic semantic) => semantic switch {
        MaterialSemantic.DiffuseMap => Tex(TextureType.Diffuse),
        MaterialSemantic.NormalMap => Tex(TextureType.Normal),
        MaterialSemantic.MetallicMap => Tex(TextureType.Metallic),
        MaterialSemantic.RoughnessMap => Tex(TextureType.Roughness),
        MaterialSemantic.AOMap => Tex(TextureType.AO),
        MaterialSemantic.EmissiveMap => Tex(TextureType.Emissive),

        MaterialSemantic.BaseColorFactor => new MaterialPropertyBinding {
            ColorComponents = 4,
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
            (d, _) => d.Metallic ?? 0f, (d, v, dflt) => d.Metallic = v == dflt ? null : v),
        MaterialSemantic.RoughnessFactor => Scalar(
            (d, dflt) => d.Roughness ?? dflt,
            (d, v, dflt) => d.Roughness = v == dflt ? null : v),
        MaterialSemantic.NormalStrength => Scalar(
            (d, dflt) => d.NormalStrength ?? dflt,
            (d, v, dflt) => d.NormalStrength = v == dflt ? null : v),
        MaterialSemantic.Opacity => Scalar(
            (d, _) => d.Opacity,
            (d, v, _) => d.Opacity = v),

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
            d => MaterialLoader.ResolvePackedOrm(d) ? 1f : 0f, (d, v) => d.PackedOrm = v != 0f),
        MaterialSemantic.Cutout => Flag(
            d => MaterialLoader.ResolveCutout(d) ? 1f : 0f, (d, v) => d.Cutout = v != 0f),

        _ => null,
    };

    static MaterialPropertyBinding Tex(TextureType t) => new() { TextureKey = t.ToString() };

    static MaterialPropertyBinding Scalar(Func<MaterialDefinition, float, float> get,
        Action<MaterialDefinition, float, float> set) =>
        new() { getFloat = get, setFloat = set };

    static MaterialPropertyBinding Flag(Func<MaterialDefinition, float> get,
        Action<MaterialDefinition, float> set) =>
        new() { IsBool = true, getFloat = (d, _) => get(d), setFloat = (d, v, _) => set(d, v) };

    public string CustomTextureKey { get; private init; }

    public static MaterialPropertyBinding ForCustom(ShaderProperty prop) => prop.Type switch {
        ShaderPropertyType.Texture2D => new MaterialPropertyBinding { CustomTextureKey = prop.Name },
        ShaderPropertyType.Color or ShaderPropertyType.Vector => new MaterialPropertyBinding {
            ColorComponents = prop.Type == ShaderPropertyType.Color ? 4 : 4,
            getVector = (d, dflt) => d.CustomVectors is not null && d.CustomVectors.TryGetValue(prop.Name, out var a) && a is { Length: >= 1 }
                ? new SysVec4(a[0], a.Length > 1 ? a[1] : 0f, a.Length > 2 ? a[2] : 0f, a.Length > 3 ? a[3] : dflt.W) : dflt,
            setVector = (d, v, dflt) => {
                if (v == dflt) { d.CustomVectors?.Remove(prop.Name); return; }
                (d.CustomVectors ??= new()).Remove(prop.Name);
                d.CustomVectors[prop.Name] = [v.X, v.Y, v.Z, v.W];
            },
        },
        _ => new MaterialPropertyBinding {
            getFloat = (d, dflt) => d.CustomFloats is not null && d.CustomFloats.TryGetValue(prop.Name, out var f) ? f : dflt,
            setFloat = (d, v, dflt) => {
                if (v == dflt) { d.CustomFloats?.Remove(prop.Name); return; }
                (d.CustomFloats ??= new())[prop.Name] = v;
            },
        },
    };
}
