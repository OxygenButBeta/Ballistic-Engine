using BallisticEngine.AssetPipeline;
using BallisticEngine.AssetPipeline.Loaders;
using BallisticEngine.Serialization;

namespace BallisticEngine.Editor;

// Single home for scene file operations (New / Open / Save / Save As), shared by the menu bar, the
// asset browser and the inspector so the load/save logic isn't copied three times. Tracks the asset
// path of the currently open scene so Save knows where to write. Loading/saving clears the dirty
// flag and resets undo history.
internal static class SceneCommands {
    // Project-relative path ("Assets/...") of the open scene, or null for an unsaved/new scene.
    public static string CurrentScenePath { get; private set; }

    // Opening a heavy scene is split in two so the window never freezes:
    //   1. PREFETCH (worker Task): decode the scene's meshes/textures to CPU data in parallel — the
    //      slow part (disk read + Deflate inflate). The busy overlay animates throughout.
    //   2. APPLY (render thread, PumpPendingOpen): deserialize + GL-upload. With the cache warm, the
    //      loaders only do the fast GL upload, so the main-thread stall is brief.
    static volatile bool prefetchDone;   // worker -> render thread handoff: prefetch finished, apply now
    static string pendingApplyPath;      // scene to apply once prefetch completes
    static string pendingApplyYaml;      // its YAML (read once, off the render thread)
    static int applyDelayFrames;         // frames left before the apply, so the final status presents

    public static bool IsLoading { get; private set; }
    public static string LoadingStatus { get; private set; }

    // Second line for the busy overlay — says what's actually happening in the current stage.
    public static string LoadingDetail { get; private set; }

    // Requests a scene open. Kicks the background prefetch immediately; the actual scene swap happens
    // on a later PumpPendingOpen (render thread) once the prefetch finishes.
    public static void Open(string assetPath) {
        // Ignore a new open while one is already loading — its prefetch task still owns AssetDataCache.
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
        applyDelayFrames = 2;
        prefetchDone = false;
        pendingApplyPath = assetPath;
        pendingApplyYaml = File.ReadAllText(absolute);

        var yaml = pendingApplyYaml;
        Task.Run(() => {
            try {
                AssetDataCache.Clear(); // drop any leftovers from a prior load
                AssetDatabase.PrefetchSceneData(yaml,
                    (done, total) => LoadingStatus = $"Loading assets {done}/{total}...");
            }
            catch (Exception exception) {
                Debugging.LogError($"Scene prefetch failed: {exception.Message}");
            }
            finally {
                prefetchDone = true; // even on failure: apply synchronously (loaders fall back to disk)
            }
        });
    }

    // Runs on the render thread each frame. Once the background prefetch has finished, performs the
    // deserialize + GL upload (now fast, off the warm cache). Returns true ONLY on the frame it
    // actually applies the scene, so the editor runs its post-load refresh exactly once. While the
    // prefetch is still running, SceneCommands.IsLoading keeps the overlay up (checked separately).
    public static bool PumpPendingOpen() {
        if (pendingApplyPath is null || !prefetchDone)
            return false;

        // The apply below blocks the render thread BEFORE this frame's buffer swap (the window
        // presents after OnRender returns), so whatever is on screen now stays up for the whole
        // stall. Two-frame countdown: set an honest status, let it draw AND present, then apply.
        if (applyDelayFrames > 0) {
            if (applyDelayFrames == 2) {
                LoadingStatus = "Building scene...";
                LoadingDetail = "Uploading to the GPU — the window may pause briefly.";
            }
            applyDelayFrames--;
            return false;
        }

        var path = pendingApplyPath;
        var yaml = pendingApplyYaml;
        pendingApplyPath = null;
        pendingApplyYaml = null;
        try {
            ApplyNow(path, yaml);
        }
        finally {
            IsLoading = false;
            AssetDataCache.Clear(); // release any prefetched-but-unused CPU data
        }
        return true;
    }

    static void ApplyNow(string assetPath, string yaml) {
        if (SceneManager.IsPlaying)
            SceneManager.StopPlay();

        SceneManager.GetCurrentScene().Clear();
        // Defensively clear every render set too — scene.Clear()'s per-component OnDetach is best-effort,
        // and a leaked renderer keeps DRAWING the old scene's meshes after the switch (the reported bug).
        SceneManager.ClearAllRenderSets();
        SceneSerializer.Deserialize(yaml);
        CurrentScenePath = assetPath;
        EditorUndo.Clear();
        RememberScene(assetPath);
    }

    // Empties the current scene. Clears undo + dirty state; the scene has no file yet.
    public static void New() {
        if (SceneManager.IsPlaying)
            SceneManager.StopPlay();

        SceneManager.GetCurrentScene().Clear();
        SceneManager.ClearAllRenderSets();
        CurrentScenePath = null;
        EditorUndo.Clear();
        RememberScene(null); // no file to reopen — next launch falls back to the StartupScene
    }

    // Saves to the current scene path (falling back to the project's StartupScene for the very first
    // session before a scene has been explicitly opened). No-op with a warning if there's nowhere to
    // write yet — the caller should offer Save As.
    public static bool Save() {
        var path = CurrentScenePath ?? AssetDatabase.Project.Manifest.StartupScene;
        if (string.IsNullOrEmpty(path)) {
            Debugging.LogWarning("No scene file to save to. Use Save As or set a startup scene.");
            return false;
        }

        SaveAs(path);
        return true;
    }

    public static void SaveAs(string assetPath) {
        SceneSerializer.Save(SceneManager.GetCurrentScene(), AssetDatabase.Project.ResolveAbsolute(assetPath));
        CurrentScenePath = assetPath;
        EditorUndo.MarkClean();
        RememberScene(assetPath);
    }

    // Adopts a path as the current scene without reloading — used when the editor loads the startup
    // scene at launch so Save targets the right file.
    public static void SetCurrent(string assetPath) {
        CurrentScenePath = assetPath;
        RememberScene(assetPath);
    }

    // Persists this scene as the project's last-opened scene so reopening the editor restores it.
    // Keyed by the project root (Path.GetFullPath in BallisticProject.Open guarantees a stable key).
    static void RememberScene(string assetPath) =>
        EditorPrefs.SetLastScene(AssetDatabase.Project.RootPath, assetPath);
}
