namespace BallisticEngine;

[EngineService]
public class SceneManager {
    public static HDCamera RenderCamera { get; set; }

    // When false (edit mode), the scene renders but components do not tick.
    // Phase 2 builds StartPlay/StopPlay (snapshot + restore) on top of this.
    public static bool IsPlaying { get; private set; }
    public static void SetPlaying(bool playing) => IsPlaying = playing;

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
}