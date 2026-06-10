namespace BallisticEngine;

// Standalone player host: brings the engine up, then runs the play-mode loop.
public sealed class BEngineEntry {
    readonly EngineLoop loop;

    public BEngineEntry(IBallisticEngineRuntime runtime, string projectPath) {
        _ = new EngineBootstrap(runtime, projectPath);
        loop = new EngineLoop(runtime);

        // The player runs game logic immediately.
        SceneManager.SetPlaying(true);
    }

    public void Run() => loop.Run();
}
