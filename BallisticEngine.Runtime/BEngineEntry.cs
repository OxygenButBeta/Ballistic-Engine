namespace BallisticEngine;

// Standalone player host: brings the engine up, loads the startup scene, then runs play mode.
public sealed class BEngineEntry {
    readonly EngineLoop loop;

    public BEngineEntry(IBallisticEngineRuntime runtime, string projectPath) {
        EngineBootstrap bootstrap = new(runtime, projectPath);
        loop = new EngineLoop(runtime);

        // Build the scene in edit mode, then enter play (fires component lifecycle).
        bootstrap.LoadStartupScene();
        SceneManager.StartPlay();
    }

    public void Run() => loop.Run();
}
