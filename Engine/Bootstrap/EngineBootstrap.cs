using BallisticEngine.AssetPipeline;
using BallisticEngine.Serialization;

namespace BallisticEngine;

// Brings the engine up to a runnable state for any host (runtime player or editor):
//   bind runtime services -> install [EngineService]s -> open project + import assets -> init renderer.
//
// It does NOT wire the update/render loop or load any scene content — the host decides
// how to drive frames (BEngineEntry for the player, EditorApplication for the editor).
public sealed class EngineBootstrap {
    public IBallisticEngineRuntime Runtime { get; }
    public BallisticProject Project { get; }

    public EngineBootstrap(IBallisticEngineRuntime runtime, string projectPath) {
        Runtime = runtime;
        SystemAPI.Bind(runtime);

        // [EngineService] types (SceneManager, EngineConfigurationAsset) live in THIS library,
        // not the host exe — scan the engine assembly, not the entry assembly.
        SingleServiceInstaller.InstallAllInAssemblies(typeof(SceneManager).Assembly);

        // Discover Behaviour types for scene (de)serialization and the editor's Add Component menu.
        // Scan the engine assembly plus the host (host may define its own components).
        ComponentRegistry.Build(typeof(SceneManager).Assembly, runtime.GetType().Assembly);

        Project = BallisticProject.Open(projectPath);
        AssetDatabase.Initialize(Project);
        AssetDatabase.Refresh();

        // Play/Stop uses the scene serializer to snapshot edit-mode state and restore it.
        SceneManager.SnapshotProvider = SceneSerializer.Serialize;
        SceneManager.SnapshotRestorer = (_, yaml) => SceneSerializer.Deserialize(yaml);

        runtime.RenderAsset.Initialize();
    }

    // Loads the project's StartupScene (if set) into the current scene, in edit mode.
    public void LoadStartupScene() {
        var startup = Project.Manifest.StartupScene;
        if (string.IsNullOrEmpty(startup))
            return;

        SceneSerializer.Load(Project.ResolveAbsolute(startup));
    }
}
