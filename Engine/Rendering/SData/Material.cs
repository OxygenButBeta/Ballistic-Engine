using OpenTK.Mathematics;

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

    // Scalar PBR factors (glTF semantics): BaseColorFactor tints the albedo map, Metallic/
    // RoughnessFactor multiply their maps (or stand alone when the slot has no texture).
    // MetallicFactor defaults to 0 so an untextured material is a dielectric, not chrome.
    public Vector4 BaseColorFactor { get; set; } = Vector4.One;
    public float MetallicFactor { get; set; }
    public float RoughnessFactor { get; set; } = 1f;

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

    // Render both faces (no backface culling) without alpha-testing — for geometry with untrusted
    // winding, e.g. pbrt imports (left-handed: their winding is opposite the engine's).
    public bool DoubleSided { get; set; }

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
            BaseColorFactor = BaseColorFactor,
            MetallicFactor = MetallicFactor,
            RoughnessFactor = RoughnessFactor,
            NormalStrength = NormalStrength,
            NormalFlipY = NormalFlipY,
            Transparent = Transparent,
            Opacity = Opacity,
            PackedOrm = PackedOrm,
            Cutout = Cutout,
            DoubleSided = DoubleSided,
        };
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
