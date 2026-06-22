using BallisticEngine.Serialization;

namespace BallisticEngine.AssetPipeline.Loaders;

public static class DataAssetLoader {
    public static DataAsset Load(BallisticProject project, string assetPath, Type requestedType) {
        string yaml = ContentText.Read(project, assetPath);
        if (yaml is null) {
            Debugging.LogError($"'{assetPath}': data asset not found.");
            return null;
        }

        try {
            return DataAssetSerializer.Deserialize(yaml, requestedType);
        }
        catch (Exception e) {
            Debugging.LogError($"'{assetPath}': failed to parse data asset — {e.Message}");
            return null;
        }
    }
}
