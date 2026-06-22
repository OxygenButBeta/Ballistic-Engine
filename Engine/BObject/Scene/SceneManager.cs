namespace BallisticEngine;

[EngineService]
public class SceneManager {
    public static HDCamera RenderCamera { get; set; }

    public static bool IsPlaying { get; private set; }
    public static void SetPlaying(bool playing) => IsPlaying = playing;

    public static bool IsPaused { get; set; }
    static bool stepRequested;
    public static void StepFrame() {
        if (IsPlaying && IsPaused)
            stepRequested = true;
    }

    public static Func<Scene, string> SnapshotProvider { get; set; }
    public static Action<Scene, string> SnapshotRestorer { get; set; }

    static string snapshot;

    internal static bool SuppressPlayLifecycle;

    public static Func<string> PlayBlocked { get; set; }

    public static Action<string> SceneLoader { get; set; }
    public static IReadOnlyList<string> BuildScenes { get; set; } = [];

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
        ClearAllRenderSets();

        SceneLoader(projectRelativePath);

        if (wasPlaying)
            StartPlay();
    }

    readonly HashSet<Scene> activeScenes = new(capacity: 5);
    static SceneManager instance;

    public SceneManager() {
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

    public static event Action RenderSetsCleared;

    public static void ClearAllRenderSets() {
        RuntimeSet<IStaticMeshRenderer>.Clear();
        RuntimeSet<PointLight>.Clear();
        RuntimeSet<SpotLight>.Clear();
        RuntimeSet<TrailRenderer>.Clear();
        RuntimeSet<IRibbonSource>.Clear();
        RuntimeSet<ParticleSystem>.Clear();
        DirectionalLight.Clear();
        RenderSetsCleared?.Invoke();
    }

    public static Scene GetCurrentScene() {
        if (instance.activeScenes.Count == 0) {
            throw new InvalidOperationException("No active scenes available.");
        }

        return instance.activeScenes.Last();
    }

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

        if (IsPaused) {
            if (!stepRequested)
                return;
            stepRequested = false;
        }

        Physics.Advance(delta, FixedTickScenes);

        Network.Manager?.PollTransport();

        Coroutine.Tick(delta);

        foreach (Scene scene in instance.activeScenes)
            scene.Update(in delta);
    }

    static readonly Action<float> FixedTickScenes = step => {
        foreach (Scene scene in instance.activeScenes)
            scene.FixedUpdate(in step);

        Network.Manager?.PredictTick(step);
    };

    public static void StartPlay() {
        if (IsPlaying)
            return;

        if (PlayBlocked?.Invoke() is { } reason) {
            Debugging.LogError($"Cannot enter play mode: {reason}");
            return;
        }

        Scene scene = GetCurrentScene();
        snapshot = SnapshotProvider?.Invoke(scene);

        Physics.BeginPlay();
        CoroutineRunner.Reset();
        IsPlaying = true;

        if (GamePhaseRunner.HasGameMode(scene))
            GamePhaseRunner.Run(scene);
        else
            scene.FireBegin();
    }

    public static void StopPlay() {
        if (!IsPlaying)
            return;

        Scene scene = GetCurrentScene();
        scene.FireEnd();

        RenderCamera = null;
        scene.Clear();
        ClearAllRenderSets();
        Physics.EndPlay();
        Network.Stop();
        CoroutineRunner.Reset();

        IsPlaying = false;
        IsPaused = false;
        stepRequested = false;

        if (snapshot is not null)
            SnapshotRestorer?.Invoke(scene, snapshot);
    }
}