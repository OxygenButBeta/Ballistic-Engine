namespace BallisticEngine.AssetPipeline;

public sealed class ArtifactDatabase {
    public int Version { get; set; } = 1;
    public Dictionary<Guid, ArtifactRecord> Entries { get; set; } = new();

    public static ArtifactDatabase Load(string path) {
        if (!File.Exists(path))
            return new ArtifactDatabase();

        try {
            return PipelineJson.Read<ArtifactDatabase>(path) ?? new ArtifactDatabase();
        }
        catch (Exception exception) {
            Debugging.LogWarning($"ArtifactDB at '{path}' is unreadable ({exception.Message}); rebuilding it.");
            return new ArtifactDatabase();
        }
    }

    public void Save(string path) => PipelineJson.Write(path, this);
}
