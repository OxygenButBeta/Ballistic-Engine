namespace BallisticEngine.AssetPipeline.Loaders;

public sealed class ShaderDefinition {
    public int Version { get; set; } = 1;
    public string Vertex { get; set; }
    public string Fragment { get; set; }
    public ShaderPropertyDef[] Properties { get; set; }

    public string Surface { get; set; }
}
