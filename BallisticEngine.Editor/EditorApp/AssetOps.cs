namespace BallisticEngine.Editor;

internal static class AssetOps {
    public const string DefaultRoot = "Assets/Default";

    public static bool IsProtected(string path) =>
        path is not null &&
        (path.Equals(DefaultRoot, StringComparison.OrdinalIgnoreCase) ||
         path.StartsWith(DefaultRoot + "/", StringComparison.OrdinalIgnoreCase));

    public static void DeleteAssets(EditorState state, IReadOnlyList<(string Path, Guid Guid)> assets,
        Action onFinished = null) {
        var deleted = 0;
        foreach ((string path, _) in assets.ToArray()) {
            if (IsProtected(path)) {
                Debugging.LogWarning($"'{path}' is a read-only Default asset and can't be deleted.");
                continue;
            }
            try {
                var absolute = AssetDatabase.Project.ResolveAbsolute(path);
                if (Directory.Exists(absolute))
                    Directory.Delete(absolute, recursive: true);
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
