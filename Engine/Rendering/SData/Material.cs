
namespace BallisticEngine;

public class Material : BObject
{
    public Texture2D Diffuse { get; set; }
    public Texture2D Normal { get; set; }
    public Texture2D Metallic { get; set; }
    public Texture2D Roughness { get; set; }
    public Texture2D AO { get; set; }
    public Texture2D Emissive { get; set; }
    public Shader Shader { get; set; }

    public Vector3 EmissiveColor { get; set; } = Vector3.One;
    public float EmissiveIntensity { get; set; } = 1f;

    public bool IsEmissive { get; set; }

    public Vector4 BaseColorFactor { get; set; } = Vector4.One;
    public float MetallicFactor { get; set; }
    public float RoughnessFactor { get; set; } = 1f;

    public float SpecularReflectance { get; set; } = 0.5f;

    public float Clearcoat { get; set; }
    public float ClearcoatRoughness { get; set; } = 0.1f;

    public float NormalStrength { get; set; } = 1f;
    public bool NormalFlipY { get; set; } = true;

    public bool Transparent { get; set; }
    public float Opacity { get; set; } = 1f;

    public bool PackedOrm { get; set; }

    public bool Cutout { get; set; }

    readonly Dictionary<MaterialSemantic, object> bag = new();

    public IReadOnlyDictionary<MaterialSemantic, object> Properties => bag;

    readonly Dictionary<string, object> customBag = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, object> CustomProperties => customBag;
    public void SetCustom(string name, object value) { if (name is not null) customBag[name] = value; }

    public float GetCustomFloat(string name) =>
        customBag.TryGetValue(name, out var v) && v is float f ? f : 0f;
    public Vector4 GetCustomVector(string name) =>
        customBag.TryGetValue(name, out var v) && v is Vector4 vec ? vec : default;
    public Texture2D GetCustomTexture(string name) =>
        customBag.TryGetValue(name, out var v) ? v as Texture2D : null;

    public Texture2D GetTexture(MaterialSemantic semantic) =>
        bag.TryGetValue(semantic, out var v) ? v as Texture2D : null;

    public float GetFloat(MaterialSemantic semantic) =>
        bag.TryGetValue(semantic, out var v) && v is float f ? f : 0f;

    public Vector4 GetVector(MaterialSemantic semantic) =>
        bag.TryGetValue(semantic, out var v) && v is Vector4 vec ? vec : default;

    public void SyncBagFromTypedFields() {
        bag[MaterialSemantic.DiffuseMap] = Diffuse;
        bag[MaterialSemantic.NormalMap] = Normal;
        bag[MaterialSemantic.MetallicMap] = Metallic;
        bag[MaterialSemantic.RoughnessMap] = Roughness;
        bag[MaterialSemantic.AOMap] = AO;
        bag[MaterialSemantic.EmissiveMap] = Emissive;

        bag[MaterialSemantic.BaseColorFactor] = BaseColorFactor;
        bag[MaterialSemantic.MetallicFactor] = MetallicFactor;
        bag[MaterialSemantic.RoughnessFactor] = RoughnessFactor;
        bag[MaterialSemantic.SpecularReflectance] = SpecularReflectance;
        bag[MaterialSemantic.EmissiveColor] = new Vector4(EmissiveColor, 1f);
        bag[MaterialSemantic.EmissiveIntensity] = EmissiveIntensity;
        bag[MaterialSemantic.NormalStrength] = NormalStrength;
        bag[MaterialSemantic.NormalFlipY] = NormalFlipY ? 1f : 0f;
        bag[MaterialSemantic.Clearcoat] = Clearcoat;
        bag[MaterialSemantic.ClearcoatRoughness] = ClearcoatRoughness;
        bag[MaterialSemantic.Transparent] = Transparent ? 1f : 0f;
        bag[MaterialSemantic.Opacity] = Opacity;
        bag[MaterialSemantic.PackedOrm] = PackedOrm ? 1f : 0f;
        bag[MaterialSemantic.Cutout] = Cutout ? 1f : 0f;
        bag[MaterialSemantic.IsEmissive] = IsEmissive ? 1f : 0f;
    }

    public void SyncTypedFieldsFromBag() {
        if (bag.TryGetValue(MaterialSemantic.DiffuseMap, out var d)) Diffuse = d as Texture2D;
        if (bag.TryGetValue(MaterialSemantic.NormalMap, out var n)) Normal = n as Texture2D;
        if (bag.TryGetValue(MaterialSemantic.MetallicMap, out var m)) Metallic = m as Texture2D;
        if (bag.TryGetValue(MaterialSemantic.RoughnessMap, out var r)) Roughness = r as Texture2D;
        if (bag.TryGetValue(MaterialSemantic.AOMap, out var a)) AO = a as Texture2D;
        if (bag.TryGetValue(MaterialSemantic.EmissiveMap, out var e)) Emissive = e as Texture2D;

        if (bag.TryGetValue(MaterialSemantic.BaseColorFactor, out var bc) && bc is Vector4 bcv) BaseColorFactor = bcv;
        if (bag.TryGetValue(MaterialSemantic.MetallicFactor, out var mf) && mf is float mff) MetallicFactor = mff;
        if (bag.TryGetValue(MaterialSemantic.RoughnessFactor, out var rf) && rf is float rff) RoughnessFactor = rff;
        if (bag.TryGetValue(MaterialSemantic.SpecularReflectance, out var sp) && sp is float spf) SpecularReflectance = spf;
        if (bag.TryGetValue(MaterialSemantic.EmissiveColor, out var ec) && ec is Vector4 ecv)
            EmissiveColor = new Vector3(ecv.X, ecv.Y, ecv.Z);
        if (bag.TryGetValue(MaterialSemantic.EmissiveIntensity, out var ei) && ei is float eif) EmissiveIntensity = eif;
        if (bag.TryGetValue(MaterialSemantic.NormalStrength, out var ns) && ns is float nsf) NormalStrength = nsf;
        if (bag.TryGetValue(MaterialSemantic.NormalFlipY, out var nf) && nf is float nff) NormalFlipY = nff != 0f;
        if (bag.TryGetValue(MaterialSemantic.Clearcoat, out var cc) && cc is float ccf) Clearcoat = ccf;
        if (bag.TryGetValue(MaterialSemantic.ClearcoatRoughness, out var cr) && cr is float crf) ClearcoatRoughness = crf;
        if (bag.TryGetValue(MaterialSemantic.Transparent, out var tr) && tr is float trf) Transparent = trf != 0f;
        if (bag.TryGetValue(MaterialSemantic.Opacity, out var op) && op is float opf) Opacity = opf;
        if (bag.TryGetValue(MaterialSemantic.PackedOrm, out var po) && po is float pof) PackedOrm = pof != 0f;
        if (bag.TryGetValue(MaterialSemantic.Cutout, out var cu) && cu is float cuf) Cutout = cuf != 0f;
        if (bag.TryGetValue(MaterialSemantic.IsEmissive, out var ie) && ie is float ief) IsEmissive = ief != 0f;
    }

    Material(Shader shader, Texture2D diffuse, Texture2D normal, Texture2D metallic, Texture2D roughness,
        Texture2D ao, Texture2D emissive)
    {
        Diffuse = diffuse;
        Normal = normal;
        Shader = shader;
        Metallic = metallic;
        Roughness = roughness;
        AO = ao;
        Emissive = emissive;
    }

    public static Material Create(StandardShader standardShader, Texture2D diffuse, Texture2D normal = null,
        Texture2D metallic = null, Texture2D roughness = null, Texture2D ao = null, Texture2D emissive = null)
    {
        return new Material(standardShader, diffuse, normal, metallic, roughness, ao, emissive);
    }

    public static Material Default() => new(null, null, null, null, null, null, null);

    public Material Clone() {
        var copy = new Material(Shader, Diffuse, Normal, Metallic, Roughness, AO, Emissive) {
            Name = Name + " (Instance)",
            EmissiveColor = EmissiveColor,
            EmissiveIntensity = EmissiveIntensity,
            IsEmissive = IsEmissive,
            BaseColorFactor = BaseColorFactor,
            MetallicFactor = MetallicFactor,
            RoughnessFactor = RoughnessFactor,
            SpecularReflectance = SpecularReflectance,
            Clearcoat = Clearcoat,
            ClearcoatRoughness = ClearcoatRoughness,
            NormalStrength = NormalStrength,
            NormalFlipY = NormalFlipY,
            Transparent = Transparent,
            Opacity = Opacity,
            PackedOrm = PackedOrm,
            Cutout = Cutout,
        };
        foreach (var kv in bag)
            copy.bag[kv.Key] = kv.Value;
        foreach (var kv in customBag)
            copy.customBag[kv.Key] = kv.Value;
        return copy;
    }

    public void Activate()
    {
        Shader.Activate();
        if (ReferenceEquals(this, LastActivatedMaterial))
            return;
        LastActivatedMaterial = this;
        Diffuse.Activate();
        (Metallic ?? DefaultTextures.Neutral(TextureType.Metallic)).Activate();
        (Normal ?? DefaultTextures.Neutral(TextureType.Normal)).Activate();
        (AO ?? DefaultTextures.Neutral(TextureType.AO)).Activate();
        (Roughness ?? DefaultTextures.Neutral(TextureType.Roughness)).Activate();
        (Emissive ?? DefaultTextures.Neutral(TextureType.Emissive)).Activate();
    }

    public void Deactivate()
    {
        if (!ReferenceEquals(this, LastActivatedMaterial))
            return;

        Shader.Deactivate();
        Diffuse.Deactivate();
        Normal?.Deactivate();
        Metallic?.Deactivate();
        Roughness?.Deactivate();
        AO?.Deactivate();
        Emissive?.Deactivate();
        LastActivatedMaterial = null;
    }

    static Material LastActivatedMaterial;
}
