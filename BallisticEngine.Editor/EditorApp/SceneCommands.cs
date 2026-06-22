using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.Serialization;

namespace BallisticEngine.Editor;

internal static class SceneCommands {
    public static string CurrentScenePath { get; private set; }

    static volatile bool prefetchDone;
    static string pendingApplyPath;
    static string pendingApplyYaml;

    public static bool IsLoading { get; private set; }
    public static string LoadingStatus { get; private set; }

    public static string LoadingDetail { get; private set; }

    public static void Open(string assetPath) {
        if (IsLoading)
            return;

        var absolute = AssetDatabase.Project.ResolveAbsolute(assetPath);
        if (!File.Exists(absolute)) {
            Debugging.LogError($"Scene file not found: '{absolute}'.");
            return;
        }

        IsLoading = true;
        LoadingStatus = $"Opening {Path.GetFileNameWithoutExtension(assetPath)}...";
        LoadingDetail = "Decoding scene assets on background threads.";
        prefetchDone = false;
        pendingApplyPath = assetPath;
        pendingApplyYaml = File.ReadAllText(absolute);

        var yaml = pendingApplyYaml;
        Task.Run(() => {
            try {
                AssetDataCache.Clear();
                AssetDatabase.PrefetchSceneData(yaml,
                    (done, total) => LoadingStatus = $"Loading assets {done}/{total}...");
            }
            catch (Exception exception) {
                Debugging.LogError($"Scene prefetch failed: {exception.Message}");
            }
            finally {
                prefetchDone = true;
            }
        });
    }

    public static bool PumpPendingOpen() {
        if (pendingApplyPath is null || !prefetchDone)
            return false;

        var path = pendingApplyPath;
        var yaml = pendingApplyYaml;
        pendingApplyPath = null;
        pendingApplyYaml = null;
        try {
            ApplyNow(path, yaml);
        }
        finally {
            IsLoading = false;
            AssetDataCache.Clear();
        }
        return true;
    }

    static void ApplyNow(string assetPath, string yaml) {
        if (SceneManager.IsPlaying)
            SceneManager.StopPlay();

        SceneManager.GetCurrentScene().Clear();
        SceneManager.ClearAllRenderSets();
        MeshUploadQueue.Clear();

        Mesh.DeferUpload = true;
        try {
            SceneSerializer.Deserialize(yaml);
        }
        finally {
            Mesh.DeferUpload = false;
        }
        CurrentScenePath = assetPath;
        EditorUndo.Clear();
        RememberScene(assetPath);
    }

    public static void New() {
        if (SceneManager.IsPlaying)
            SceneManager.StopPlay();

        SceneManager.GetCurrentScene().Clear();
        SceneManager.ClearAllRenderSets();
        CurrentScenePath = null;
        EditorUndo.Clear();
        RememberScene(null);
    }

    public static bool Save() {
        if (SceneManager.IsPlaying) {
            Debugging.LogWarning("Can't save while in play mode — stop play first (the edit scene would be overwritten with play state).");
            return false;
        }

        var path = CurrentScenePath ?? AssetDatabase.Project.Manifest.StartupScene;
        if (string.IsNullOrEmpty(path)) {
            Debugging.LogWarning("No scene file to save to. Use Save As or set a startup scene.");
            return false;
        }

        SaveAs(path);
        return true;
    }

    public static void SaveAs(string assetPath) {
        if (SceneManager.IsPlaying) {
            Debugging.LogWarning("Can't save while in play mode — stop play first.");
            return;
        }
        SceneSerializer.Save(SceneManager.GetCurrentScene(), AssetDatabase.Project.ResolveAbsolute(assetPath));
        CurrentScenePath = assetPath;
        EditorUndo.MarkClean();
        RememberScene(assetPath);
    }

    public static void SetCurrent(string assetPath) {
        CurrentScenePath = assetPath;
        RememberScene(assetPath);
    }

    static void RememberScene(string assetPath) =>
        EditorPrefs.SetLastScene(AssetDatabase.Project.RootPath, assetPath);
}
