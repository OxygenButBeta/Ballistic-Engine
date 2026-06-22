namespace BallisticEngine.AssetPipeline;

public sealed class ArtifactRecord {
    public string SourcePath { get; set; }
    public string ContentHash { get; set; }
    public string SettingsHash { get; set; }
    public int ImporterVersion { get; set; }
    public long FileSize { get; set; }
    public DateTime MtimeUtc { get; set; }

    public string Artifact { get; set; }
}
