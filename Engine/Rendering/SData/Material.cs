
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

    // True when this material emits light — an emissive MAP, or an authored emissive COLOR (the
    // loader sets it; EmissiveColor defaults to white so the color alone can't be the signal). The
    // renderer's HasEmissive gates on this, so a COLOR-ONLY emissive (neon, screens, area lights,
    // the Cornell light) emits, not just textured emissives. Default false (a plain material is
    // not emissive even though EmissiveColor defaults to white).
    public bool IsEmissive { get; set; }

    // Scalar PBR factors (glTF semantics): BaseColorFactor tints the albedo map, Metallic/
    // RoughnessFactor multiply their maps (or stand alone when the slot has no texture).
    // MetallicFactor defaults to 0 so an untextured material is a dielectric, not chrome.
    public Vector4 BaseColorFactor { get; set; } = Vector4.One;
    public float MetallicFactor { get; set; }
    public float RoughnessFactor { get; set; } = 1f;

    // Dielectric specular reflectance (glTF KHR_materials_specular): the F0 of a non-metal at
    // normal incidence is 0.08 * SpecularReflectance, so 0.5 = F0 0.04 = the default 4% dielectric
    // (byte-identical to the old hardcoded 0.04). Raise for gems/water/varnish (higher IOR), lower
    // for chalk/cloth. Metals ignore it (F0 = albedo). The renderer multiplies F0's dielectric base.
    public float SpecularReflectance { get; set; } = 0.5f;

    // CLEARCOAT (glTF KHR_materials_clearcoat): a thin transparent lacquer layer over the base —
    // car paint, varnish, wet surfaces. A second GGX specular lobe (fixed F0 ~0.04) with its own
    // low roughness, plus it attenuates the base layer by its Fresnel. 0 = no coat (default, off).
    public float Clearcoat { get; set; }
    public float ClearcoatRoughness { get; set; } = 0.1f;

    // Normal map controls. FlipY = DirectX-convention map (G down), the common game-content case.
    public float NormalStrength { get; set; } = 1f;
    public bool NormalFlipY { get; set; } = true;

    // Transparent materials render in a sorted back-to-front pass with alpha blending.
    public bool Transparent { get; set; }
    public float Opacity { get; set; } = 1f;

    // Metallic texture is ORM-packed (occlusion, roughness, metallic) — Falcor/glTF style
    // "Specular" maps. The shader then reads metallic from B, roughness from G, occlusion from R.
    public bool PackedOrm { get; set; }

    // Alpha-cutout (masked): diffuse alpha < 0.5 discards, and the surface renders
    // double-sided (foliage cards, fences, grates).
    public bool Cutout { get; set; }

    // ---- Property bag (shader-declared properties; staged migration) ----
    //
    // Keyed by MaterialSemantic, this is the SHADER-DECLARED view of the material's RESOLVED values
    // (textures by ref, scalars/flags as float/Vector4). During the staged migration it lives
    // ALONGSIDE the typed fields above and is kept in sync with them — the renderer/editor still read
    // the typed fields today; Stage 3 flips them to read the bag. The bag is DERIVED from the typed
    // fields (SyncBagFromTypedFields), never the other way for defaults: MaterialLoader.ApplyScalars
    // stays the sole authority that resolves sibling-dependent defaults (the metallic-map case), so
    // the bag can't drift from it. Empty until SyncBagFromTypedFields() runs (loader calls it).
    readonly Dictionary<MaterialSemantic, object> bag = new();

    public IReadOnlyDictionary<MaterialSemantic, object> Properties => bag;

    public Texture2D GetTexture(MaterialSemantic semantic) =>
        bag.TryGetValue(semantic, out var v) ? v as Texture2D : null;

    public float GetFloat(MaterialSemantic semantic) =>
        bag.TryGetValue(semantic, out var v) && v is float f ? f : 0f;

    public Vector4 GetVector(MaterialSemantic semantic) =>
        bag.TryGetValue(semantic, out var v) && v is Vector4 vec ? vec : default;

    // Project the typed fields into the bag (semantic-keyed). Called after ApplyScalars so the bag
    // reflects fully-resolved values, including the conditional defaults. bool flags pack as 1f/0f
    // to match how the declared properties model them (see StandardShaderProperties).
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

    // Inverse of SyncBagFromTypedFields: push the bag back onto the typed fields. Used by the editor
    // (Stage 4) when a property is edited through the generated inspector, so the live render — still
    // reading typed fields until Stage 3 — sees the change immediately. No default RESOLUTION here:
    // the bag already holds resolved values. Missing keys leave the field untouched.
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

    // A bare material carrying only the field DEFAULTS (no shader, no textures) — used to assert that
    // the Standard shader's declared property defaults match these (StandardShaderProperties).
    public static Material Default() => new(null, null, null, null, null, null, null);

    // Deep-copies this material's authored state into a NEW instance (Unity's renderer.material
    // clone). Textures and the shader are shared BY REFERENCE (they're GPU assets — duplicating them
    // would be wasteful and wrong); the scalar/flag PBR parameters are copied so the clone can be
    // tuned per-renderer without touching the shared asset. The clone is NOT registered with the
    // AssetDatabase, so it serializes as null (a runtime-only override, like Unity's instanced mats).
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
        // Mirror the bag onto the clone (values are immutable structs / shared texture refs).
        foreach (var kv in bag)
            copy.bag[kv.Key] = kv.Value;
        return copy;
    }

    public void Activate()
    {
        // Always re-activate the shader: other passes (skybox, post-process) bind their own
        // programs between draws, and uniform uploads target whatever program is current.
        Shader.Activate();
        if (ReferenceEquals(this, LastActivatedMaterial))
            return;
        LastActivatedMaterial = this;
        // Unassigned slots bind neutral stand-ins; leaving a unit unbound would sample
        // whatever the previous material left there (draw-order-dependent shading).
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
