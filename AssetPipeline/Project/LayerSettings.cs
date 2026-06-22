using BallisticEngine.AssetPipeline;

namespace BallisticEngine;

public static class LayerSettings {
    public sealed class SettingsData {
        public List<string> Tags { get; set; } = new();
        public List<string> Layers { get; set; } = new();
        public List<bool> CollisionMatrix { get; set; } = new();
    }

    static string PathFor(BallisticProject project) =>
        Path.Combine(project.RootPath, "ProjectSettings", "TagsAndLayers.json");

    public static void Load(BallisticProject project) {
        string path = PathFor(project);
        if (!File.Exists(path)) {
            Save(project);
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
