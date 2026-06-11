namespace BallisticEngine.Editor;

// Batch asset file operations shared by the Asset Browser and the multi-asset Inspector.
// All operations end with one async refresh pass so the database/thumbnails stay consistent.
internal static class AssetOps {
    // Deletes the given assets (+ their .meta sidecars) and clears the selection.
    public static void DeleteAssets(EditorState state, IReadOnlyList<(string Path, Guid Guid)> assets,
        Action onFinished = null) {
        var deleted = 0;
        foreach ((string path, _) in assets.ToArray()) {
            try {
                var absolute = AssetDatabase.Project.ResolveAbsolute(path);
                if (Directory.Exists(absolute))
                    Directory.Delete(absolute, recursive: true); // its children's .meta files go with it
                else if (File.Exists(absolute))
                    File.Delete(absolute);
                var metaPath = absolute + ".meta";
                if (File.Exists(metaPath))
                    File.Delete(metaPath);
                deleted++;
            }
            catch (Exception exception) {
                Debugging.LogError($"Delete failed for '{path}': {exception.Message}");
            }
        }

        state.ClearAssetSelection();
        if (deleted > 0)
            AsyncAssetImport.Request(deleted == 1 ? "Updating assets..." : $"Removing {deleted} assets...",
                onFinished: onFinished);
    }
}
