namespace BallisticEngine.AssetPipeline.Loaders;

public static class PrefabLoader {
    public static PrefabAsset Load(BallisticProject project, string assetPath) {
        string yaml = ContentText.Read(project, assetPath);
        if (yaml is null) {
            Debugging.LogError($"'{assetPath}': prefab not found.");
            return null;
        }

        try {
            PrefabAsset prefab = PrefabAsset.FromYaml(yaml);
            if (AssetDatabase.TryGetGuid(assetPath, out Guid guid))
                prefab.SourceGuid = guid;
            return prefab;
        }
        catch (Exception e) {
            Debugging.LogError($"'{assetPath}': failed to parse prefab — {e.Message}");
            return null;
        }
    }
}
