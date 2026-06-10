using BallisticEngine.AssetPipeline;

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

        Project = BallisticProject.Open(projectPath);
        AssetDatabase.Initialize(Project);
        AssetDatabase.Refresh();

        runtime.RenderAsset.Initialize();
    }
}
