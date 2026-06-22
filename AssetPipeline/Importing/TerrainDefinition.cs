namespace BallisticEngine.AssetPipeline;

public sealed class TerrainDefinition {
    public int Version { get; set; } = 1;
    public int Resolution { get; set; } = 256;
    public float SizeX { get; set; } = 100f;
    public float SizeZ { get; set; } = 100f;
    public float HeightScale { get; set; } = 20f;

    public string Heights { get; set; }

    public string HeightmapImage { get; set; }
}
