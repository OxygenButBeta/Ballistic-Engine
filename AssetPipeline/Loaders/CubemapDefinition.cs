namespace BallisticEngine.AssetPipeline.Loaders;

public sealed class CubemapDefinition {
    public int Version { get; set; } = 1;
    public Dictionary<string, string> Faces { get; set; } = new();
    public string Equirect { get; set; }
    public int FaceSize { get; set; } = 512;
}
