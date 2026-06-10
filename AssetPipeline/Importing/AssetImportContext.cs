using System.Text.Json.Nodes;

namespace BallisticEngine.AssetPipeline;

public sealed class AssetImportContext {
    public string SourceAbsolutePath { get; init; }
    public string AssetPath { get; init; }
    public Guid Guid { get; init; }
    public JsonObject Settings { get; init; }
    public string ArtifactAbsolutePath { get; init; }
}
