using System.Text.Json.Nodes;

namespace BallisticEngine.AssetPipeline;

// Sidecar file next to every asset under Assets\: "<file>.meta".
// Holds the asset's stable GUID and its importer configuration.
public sealed class MetaFile {
    public int Version { get; set; } = 1;
    public Guid Guid { get; set; }
    public string Importer { get; set; }
    public JsonObject Settings { get; set; } = new();

    public static string PathFor(string assetAbsolutePath) => assetAbsolutePath + ".meta";

    public static MetaFile Load(string metaPath) => PipelineJson.Read<MetaFile>(metaPath);

    public void Save(string metaPath) => PipelineJson.Write(metaPath, this);

    public string SettingsHash() => ContentHash.HashString(Settings?.ToJsonString() ?? "{}");
}
