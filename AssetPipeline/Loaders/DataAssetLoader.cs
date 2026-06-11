using BallisticEngine.Serialization;

namespace BallisticEngine.AssetPipeline.Loaders;

// Loads a .asset (DataAsset YAML) into the concrete DataAsset subtype named in the file. Pack-aware
// via ContentText. `requestedType` is the T from AssetDatabase.Load<T> so the loader can validate
// the stored type is assignable to it. Never throws — logs + returns null on any failure.
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
