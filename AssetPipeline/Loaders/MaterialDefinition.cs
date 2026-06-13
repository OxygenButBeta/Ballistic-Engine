namespace BallisticEngine.AssetPipeline.Loaders;

// .mat asset: { "version": 1, "shader": "<.shader ref>", "textures": { "Diffuse": "<ref>", ... },
//              "transparent": false, "opacity": 1.0, "emissiveColor": [r,g,b], "emissiveIntensity": 1.0 }
// Texture keys are TextureType names. Read by MaterialLoader, written by ModelImporter when it
// generates materials from a model's source materials.
public sealed class MaterialDefinition {
    public int Version { get; set; } = 1;
    public string Shader { get; set; }
    public Dictionary<string, string> Textures { get; set; } = new();

    public bool Transparent { get; set; }
    public float Opacity { get; set; } = 1f;
    public float[] EmissiveColor { get; set; }
    public float EmissiveIntensity { get; set; } = 1f;

    // Scalar PBR factors from the source material (glTF-style). null = unstated; the loader
    // falls back to its defaults. BaseColor is a linear RGBA tint multiplying the albedo map.
    public float[] BaseColor { get; set; }
    public float? Metallic { get; set; }
    public float? Roughness { get; set; }

    // Normal map controls. NormalFlipY: DirectX-convention maps (G points down) need the flip,
    // OpenGL-convention maps don't. null = DirectX assumed (the common case for game content).
    public float? NormalStrength { get; set; }
    public bool? NormalFlipY { get; set; }

    // Metallic texture is (occlusion, roughness, metallic) packed RGB (Falcor/glTF "Specular"
    // maps). null = auto-detect from the texture's file name ("spec").
    public bool? PackedOrm { get; set; }

    // Alpha-cutout (masked) material: pixels below 0.5 diffuse alpha are discarded and the
    // surface renders double-sided (foliage cards, fences). null = auto-detect from the
    // diffuse texture's file name.
    public bool? Cutout { get; set; }

    // Render both faces (disable backface culling) WITHOUT alpha-testing — for geometry whose
    // triangle winding can't be trusted (e.g. pbrt scenes, which are left-handed: their winding is
    // opposite the engine's, so single-sided culling hides every inward face). null = default (cull).
    public bool? DoubleSided { get; set; }
}
