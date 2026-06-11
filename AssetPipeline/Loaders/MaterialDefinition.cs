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
}
