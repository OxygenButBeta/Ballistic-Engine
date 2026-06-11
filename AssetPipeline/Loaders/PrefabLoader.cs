namespace BallisticEngine.AssetPipeline.Loaders;

// Loads a .prefab (YAML, same shape as a scene's entities block) into a PrefabAsset. Pack-aware via
// ContentText so a shipped player reads it from the mounted content pack. Never throws — logs and
// returns null on a missing/garbled file, matching the asset system's conventions.
public static class PrefabLoader {
    public static PrefabAsset Load(BallisticProject project, string assetPath) {
        string yaml = ContentText.Read(project, assetPath);
        if (yaml is null) {
            Debugging.LogError($"'{assetPath}': prefab not found.");
            return null;
        }

        try {
            return PrefabAsset.FromYaml(yaml);
        }
        catch (Exception e) {
            Debugging.LogError($"'{assetPath}': failed to parse prefab — {e.Message}");
            return null;
        }
    }
}
