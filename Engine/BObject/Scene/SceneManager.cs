namespace BallisticEngine;

[EngineService]
public class SceneManager {
    public static HDCamera RenderCamera { get; set; }

    // When false (edit mode), the scene renders but components do not tick.
    public static bool IsPlaying { get; private set; }
    public static void SetPlaying(bool playing) => IsPlaying = playing;

    // Phase 5 wires these to the YAML scene serializer so Play snapshots edit-mode state
    // and Stop restores it. Until then they may be null and play/stop is one-way.
    public static Func<Scene, string> SnapshotProvider { get; set; }
    public static Action<Scene, string> SnapshotRestorer { get; set; }

    static string snapshot;

    readonly HashSet<Scene> activeScenes = new(capacity: 5);
    static SceneManager instance;

    public SceneManager() {
        // Generate the first scene
        instance = this;
        InsertScene(new Scene {
            Name = "Default Scene"
        });
    }

    public static void InsertScene(Scene scene) {
        if (scene == null) {
            throw new ArgumentNullException(nameof(scene), "Scene cannot be null");
        }

        instance.activeScenes.Add(scene);
    }

    public static Scene GetCurrentScene() {
        if (instance.activeScenes.Count == 0) {
            throw new InvalidOperationException("No active scenes available.");
        }

        return instance.activeScenes.Last();
    }

    public static void Update(float delta) {
        if (!IsPlaying)
            return;

        foreach (Scene scene in instance.activeScenes)
            scene.Update(in delta);
    }

    // Enter play mode: snapshot the current (edit-mode) scene, then run component lifecycle.
    public static void StartPlay() {
        if (IsPlaying)
            return;

        Scene scene = GetCurrentScene();
        snapshot = SnapshotProvider?.Invoke(scene);

        IsPlaying = true;
        scene.FireBegin();
    }

    // Leave play mode: tear down components, clear runtime state, restore the snapshot.
    public static void StopPlay() {
        if (!IsPlaying)
            return;

        Scene scene = GetCurrentScene();
        scene.FireEnd();

        RenderCamera = null;
        RuntimeSet<IStaticMeshRenderer>.Clear();
        scene.Clear();
        DirectionalLight.Instance = null;

        IsPlaying = false;

        if (snapshot is not null)
            SnapshotRestorer?.Invoke(scene, snapshot);
    }
}