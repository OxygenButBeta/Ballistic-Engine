namespace BallisticEngine.AssetPipeline;

// On-disk shape of a .terrain source file (JSON). The height field is stored as a base64
// Deflate-compressed float[] blob so the source is self-contained and round-trips through git as
// one file — the sculpt tools write the mutated heights straight back here. The importer expands
// this into the fast-load binary .bterrain artifact.
//
// HeightmapImage is an optional one-shot initializer: if Heights is empty/missing and this names
// an image asset, the importer samples it into the height field (then it can be cleared so later
// sculpting owns the data). Normal authoring leaves it null and edits Heights directly.
public sealed class TerrainDefinition {
    public int Version { get; set; } = 1;
    public int Resolution { get; set; } = 256;
    public float SizeX { get; set; } = 100f;
    public float SizeZ { get; set; } = 100f;
    public float HeightScale { get; set; } = 20f;

    // base64(Deflate(float[Resolution*Resolution])) in [0,1], row-major. Empty = flat (all zeros).
    public string Heights { get; set; }

    // Optional "Assets/..." or "guid:..." image to seed the height field from on (re)import.
    public string HeightmapImage { get; set; }
}
