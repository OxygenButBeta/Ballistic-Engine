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

        // Falcor .pyscene -> Ballistic .scene conversion (injected; the converter is in the Engine layer).
        FalcorSceneImporter.Converter = (pyscene, output) =>
            FalcorSceneConverter.Convert(pyscene, output, ResolveModelToAssetRef);

        AssetDatabase.Refresh();

        // Play/Stop uses the scene serializer to snapshot edit-mode state and restore it.
        SceneManager.SnapshotProvider = SceneSerializer.Serialize;
        SceneManager.SnapshotRestorer = (_, yaml) => SceneSerializer.Deserialize(yaml);

        runtime.RenderAsset.Initialize();
    }

    // Maps an absolute model path (from a .pyscene) to an "Assets/..." reference if it lives in the
    // project. Returns a path ref (not a guid) because the model's GUID may not be assigned yet during
    // the same refresh; the path resolves at scene-load time once the refresh completes.
    string ResolveModelToAssetRef(string absoluteModelPath) {
        if (!File.Exists(absoluteModelPath))
            return null;

        var full = Path.GetFullPath(absoluteModelPath);
        if (!full.StartsWith(Project.RootPath, StringComparison.OrdinalIgnoreCase))
            return null;

        return Project.ToAssetPath(full);
    }

    // Advances the engine one frame: ticks the clock and updates the scene (scene Update is a
    // no-op unless playing). Hosts that drive their own loop (the editor) call this.
    public void UpdateFrame(double delta) {
        Runtime.EngineTimer.Update(delta);
        SceneManager.Update((float)delta);
    }

    // Loads the project's StartupScene (if set) into the current scene, in edit mode.
    public void LoadStartupScene() {
        var startup = Project.Manifest.StartupScene;
        if (string.IsNullOrEmpty(startup))
            return;

        SceneSerializer.Load(Project.ResolveAbsolute(startup));
    }
}
