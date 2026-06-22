namespace BallisticEngine.AssetPipeline.Loaders;

public sealed class MaterialDefinition {
    public int Version { get; set; } = 1;
    public string Shader { get; set; }
    public Dictionary<string, string> Textures { get; set; } = new();

    public bool Transparent { get; set; }
    public float Opacity { get; set; } = 1f;
    public float[] EmissiveColor { get; set; }
    public float EmissiveIntensity { get; set; } = 1f;

    public float[] BaseColor { get; set; }
    public float? Metallic { get; set; }
    public float? Roughness { get; set; }

    public float? NormalStrength { get; set; }
    public bool? NormalFlipY { get; set; }

    public bool? PackedOrm { get; set; }

    public bool? Cutout { get; set; }

    public Dictionary<string, float> CustomFloats { get; set; }
    public Dictionary<string, float[]> CustomVectors { get; set; }
    public Dictionary<string, string> CustomTextures { get; set; }
}
