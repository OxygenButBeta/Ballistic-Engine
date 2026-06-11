namespace BallisticEngine;

[EngineService]
public class SceneManager {
    public static HDCamera RenderCamera { get; set; }

    // When false (edit mode), the scene renders but components do not tick.
    public static bool IsPlaying { get; private set; }
    public static void SetPlaying(bool playing) => IsPlaying = playing;

    // Play-mode pause (Unity's pause button): while paused the simulation holds — Update skips the
    // fixed/coroutine/tick passes — but the scene still renders so you can inspect a frozen frame.
    // StepFrame advances exactly one frame while paused (frame-by-frame debugging). Cleared on Stop.
    public static bool IsPaused { get; set; }
    static bool stepRequested;
    public static void StepFrame() {
        if (IsPlaying && IsPaused)
            stepRequested = true;
    }

    // Phase 5 wires these to the YAML scene serializer so Play snapshots edit-mode state
    // and Stop restores it. Until then they may be null and play/stop is one-way.
    public static Func<Scene, string> SnapshotProvider { get; set; }
    public static Action<Scene, string> SnapshotRestorer { get; set; }

    static string snapshot;

    // Set by SceneSerializer.Deserialize for its duration: while a scene rebuilds from YAML in
    // play mode (live script reload), Entity.Attach must NOT fire OnBegin/OnEnabled — member
    // values are applied after AddComponent, so user code would observe defaults. The reload
    // path fires FireBegin itself once the whole scene is up.
    internal static bool SuppressPlayLifecycle;

    // Wired by the bootstrap (Unity's "fix compile errors before entering playmode"): returns a
    // human-readable reason play is currently blocked, or null when it's allowed. Engine layering
    // keeps the script-compiler knowledge out of here — the bootstrap injects the check.
    public static Func<string> PlayBlocked { get; set; }

    // ---- Scenes-in-build (Unity-style runtime scene loading) ----------------
    //
    // SceneManager lives in the Engine layer and may not reference AssetPipeline, so the bootstrap
    // injects these the same way it does the snapshot delegates. SceneLoader reads + deserializes a
    // scene by its project-relative path into the (already-cleared) current scene; BuildScenes is the
    // ordered manifest list (index 0 = startup). Both null on a host that didn't wire scene loading.
    public static Action<string> SceneLoader { get; set; }
    public static IReadOnlyList<string> BuildScenes { get; set; } = [];

    // Loads a scene from the build list by its name (file name without extension, Unity-style:
    // LoadScene("Level2")). Logs and no-ops if no build scene matches. Works in both edit and play.
    public static void LoadScene(string name) {
        if (string.IsNullOrEmpty(name)) {
            Debugging.LogError("LoadScene: scene name is null or empty.");
            return;
        }

        var path = BuildScenes.FirstOrDefault(p =>
            string.Equals(SceneName(p), name, StringComparison.OrdinalIgnoreCase));
        if (path is null) {
            Debugging.LogError(
                $"LoadScene: no scene named '{name}' is in the build list. " +
                $"Available: {string.Join(", ", BuildScenes.Select(SceneName))}");
            return;
        }

        LoadScenePath(path);
    }

    // Loads a scene from the build list by its index (LoadScene(0) = startup scene).
    public static void LoadScene(int buildIndex) {
        if (buildIndex < 0 || buildIndex >= BuildScenes.Count) {
            Debugging.LogError(
                $"LoadScene: build index {buildIndex} out of range (0..{BuildScenes.Count - 1}).");
            return;
        }

        LoadScenePath(BuildScenes[buildIndex]);
    }

    static string SceneName(string projectRelativePath) =>
        Path.GetFileNameWithoutExtension(projectRelativePath);

    // Tears the live scene down (mirrors StopPlay's teardown so renderers/physics release their
    // sets), loads the new scene over it in edit mode, then re-enters play if we were playing.
    static void LoadScenePath(string projectRelativePath) {
        if (SceneLoader is null) {
            Debugging.LogError("LoadScene: no SceneLoader wired (host did not enable scene loading).");
            return;
        }

        bool wasPlaying = IsPlaying;
        if (wasPlaying)
            StopPlay();

        Scene scene = GetCurrentScene();
        scene.Clear();
        RuntimeSet<IStaticMeshRenderer>.Clear();
        DirectionalLight.Clear();

        SceneLoader(projectRelativePath);

        if (wasPlaying)
            StartPlay();
    }

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

    // Resolve a scene object (Entity, Behaviour, or SceneBehaviour) by its InstanceId, scanning the
    // current scene. Used by BEvent persistent listeners to bind their authored target back to a live
    // object after load. Returns null if nothing matches (target deleted / different scene) — callers
    // treat that as a missing reference and skip, Unity-style. Linear scan; listener invokes cache
    // the result, so this runs at most once per listener per resolve.
    public static BObject FindByInstanceId(Guid id) {
        if (id == Guid.Empty || instance.activeScenes.Count == 0)
            return null;

        Scene scene = GetCurrentScene();

        foreach (Entity entity in scene.Entities) {
            if (entity.InstanceId == id)
                return entity;
            foreach (Behaviour behaviour in entity.Behaviours)
                if (behaviour.InstanceId == id)
                    return behaviour;
        }

        foreach (SceneBehaviour behaviour in scene.SceneBehaviours)
            if (behaviour.InstanceId == id)
                return behaviour;

        return null;
    }

    public static void Update(float delta) {
        if (!IsPlaying)
            return;

        // Paused: hold the simulation. A single StepFrame() request lets exactly one frame through
        // (consume the request here so the next frame re-freezes). The scene still renders either way.
        if (IsPaused) {
            if (!stepRequested)
                return;
            stepRequested = false;
        }

        // Fixed-step pass first (Unity ordering): FixedTick on behaviours, then the physics
        // world steps and writes simulated poses back to transforms. Zero or more times a frame.
        // (The coroutine fixed pump runs inside Physics.Advance, before each step's FixedTick.)
        Physics.Advance(delta, FixedTickScenes);

        // Pump coroutines/async continuations BEFORE the scene Tick so a resume scheduled last frame
        // (and any await DelaySeconds that elapses this frame) runs with this frame's state, ahead of
        // the components that may depend on it.
        Coroutine.Tick(delta);

        foreach (Scene scene in instance.activeScenes)
            scene.Update(in delta);
    }

    static readonly Action<float> FixedTickScenes = step => {
        foreach (Scene scene in instance.activeScenes)
            scene.FixedUpdate(in step);
    };

    // Enter play mode: snapshot the current (edit-mode) scene, then run component lifecycle.
    public static void StartPlay() {
        if (IsPlaying)
            return;

        if (PlayBlocked?.Invoke() is { } reason) {
            Debugging.LogError($"Cannot enter play mode: {reason}");
            return;
        }

        Scene scene = GetCurrentScene();
        snapshot = SnapshotProvider?.Invoke(scene);

        Physics.BeginPlay(); // fresh world before components create bodies in OnEnabled
        CoroutineRunner.Reset(); // no coroutines carry over from a previous session
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
        Physics.EndPlay(); // after Clear so component teardown saw a live world
        CoroutineRunner.Reset(); // abandon any in-flight coroutines/awaits — they don't survive Stop
        DirectionalLight.Clear();

        IsPlaying = false;
        IsPaused = false;
        stepRequested = false;

        if (snapshot is not null)
            SnapshotRestorer?.Invoke(scene, snapshot);
    }
}