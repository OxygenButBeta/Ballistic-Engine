using BallisticEngine.AssetPipeline;

namespace BallisticEngine;

// Persists the project's tags, layer names, and collision matrix (Unity's
// ProjectSettings/TagManager.asset). Stored as ProjectSettings/TagsAndLayers.json at the project
// root so it's source-controlled with the project, not in the gitignored Library. Loaded once at
// bootstrap into TagManager/LayerManager; the editor's Tags & Layers settings panel saves it back.
//
// Lives in AssetPipeline (it owns file I/O + the project) but drives the Engine-layer managers —
// AssetPipeline may reference Engine types, the reverse is forbidden, so the bootstrap calls Load.
public static class LayerSettings {
    public sealed class SettingsData {
        public List<string> Tags { get; set; } = new();
        public List<string> Layers { get; set; } = new();      // 32 entries (index = layer)
        public List<bool> CollisionMatrix { get; set; } = new(); // upper-triangle flat (LayerManager export)
    }

    static string PathFor(BallisticProject project) =>
        Path.Combine(project.RootPath, "ProjectSettings", "TagsAndLayers.json");

    // Applies the saved settings to the managers, or seeds the file from the current defaults if it
    // doesn't exist yet (so a fresh project gets a visible, editable settings file).
    public static void Load(BallisticProject project) {
        string path = PathFor(project);
        if (!File.Exists(path)) {
            Save(project); // materialize defaults
            return;
        }

        try {
            SettingsData data = PipelineJson.Read<SettingsData>(path);
            if (data is null)
                return;

            if (data.Tags is { Count: > 0 })
                TagManager.SetTags(data.Tags);
            if (data.Layers is { Count: > 0 })
                LayerManager.SetNames(data.Layers);
            if (data.CollisionMatrix is { Count: > 0 })
                LayerManager.ImportMatrix(data.CollisionMatrix);
        }
        catch (Exception e) {
            Debugging.LogError($"Failed to load TagsAndLayers.json: {e.Message}. Using defaults.");
        }
    }

    // Snapshots the current managers to disk (called by the editor settings panel after edits).
    public static void Save(BallisticProject project) {
        string path = PathFor(project);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var data = new SettingsData {
            Tags = TagManager.Tags.ToList(),
            Layers = Enumerable.Range(0, LayerManager.LayerCount).Select(LayerManager.NameOf).ToList(),
            CollisionMatrix = LayerManager.ExportMatrix(),
        };

        try {
            PipelineJson.Write(path, data);
        }
        catch (Exception e) {
            Debugging.LogError($"Failed to save TagsAndLayers.json: {e.Message}");
        }
    }
}
