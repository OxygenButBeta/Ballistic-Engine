using System.Text.Json.Nodes;

namespace BallisticEngine.AssetPipeline;

public sealed class GuidMap {
    public int Version { get; set; } = 1;
    public Dictionary<string, string> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, MetaInfo> Meta { get; set; } = new();

    public sealed class MetaInfo {
        public string Importer { get; set; }
        public JsonObject Settings { get; set; }
    }

    public const string FileName = "guidmap.json";

    public static GuidMap Load(string path) {
        if (!File.Exists(path))
            return null;
        try {
            return PipelineJson.Read<GuidMap>(path);
        }
        catch (Exception exception) {
            Debugging.LogWarning($"guidmap at '{path}' is unreadable ({exception.Message}); ignoring.");
            return null;
        }
    }

    public void Save(string path) => PipelineJson.Write(path, this);
}
