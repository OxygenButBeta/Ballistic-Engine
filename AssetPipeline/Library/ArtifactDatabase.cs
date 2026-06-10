namespace BallisticEngine.AssetPipeline;

public sealed class ArtifactRecord {
    public string SourcePath { get; set; }
    public string ContentHash { get; set; }
    public string SettingsHash { get; set; }
    public int ImporterVersion { get; set; }
    public long FileSize { get; set; }
    public DateTime MtimeUtc { get; set; }

    // Relative to Library\, e.g. "Artifacts/<guid>.btex".
    public string Artifact { get; set; }
}

// Library\ArtifactDB.json — the import pipeline's record of what has been imported from which source state.
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
