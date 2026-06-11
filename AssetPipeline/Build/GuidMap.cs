using System.Text.Json.Nodes;

namespace BallisticEngine.AssetPipeline;

// A baked, complete asset-path -> GUID table written at BUILD time (Library\guidmap.json) so a
// shipped player can resolve "Assets/..." references WITHOUT shipping the .meta sidecars (which is
// how the GUID is normally discovered). This lets the build strip source files and metas entirely
// while the player still maps every scene/material reference to its GUID -> baked artifact.
//
// `Entries` is the path->guid table. `Meta` carries the RUNTIME-relevant slice of each asset's .meta
// (importer name + settings, keyed by guid) — without it the player has no .meta to read texture
// type from, so every texture would load as Diffuse and normal/spec maps bind to the wrong sampler
// (garbled surfaces). It deliberately omits the leaky/dev fields (content hashes, source paths).
//
// Stored as camelCase JSON via PipelineJson. Paths are project-relative with forward slashes.
public sealed class GuidMap {
    public int Version { get; set; } = 1;
    public Dictionary<string, string> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // guid (string) -> shipped import settings. Optional per asset; absent for assets with no
    // meaningful runtime settings (the player falls back to importer defaults / Diffuse).
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
