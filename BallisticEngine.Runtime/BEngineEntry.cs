using BallisticEngine.AssetPipeline;

namespace BallisticEngine;

public sealed class BEngineEntry {
    readonly EngineLoop loop;

    public BEngineEntry(IBallisticEngineRuntime runtime, string projectPath, bool playerMode = false) {
        EngineBootstrap bootstrap = new(runtime, projectPath, playerMode: playerMode);

        if (GameScripts.CompileFailed) {
            Debugging.LogError("Game scripts failed to compile — the player will not run. " +
                               "Fix the errors above and start again.");
            Environment.Exit(1);
        }

        loop = new EngineLoop(runtime);

        string sceneOverride = Environment.GetEnvironmentVariable("BALLISTIC_SCENE");
        if (string.IsNullOrEmpty(sceneOverride))
            bootstrap.LoadStartupScene();
        else
            SceneManager.SceneLoader!.Invoke(sceneOverride.Replace('\\', '/'));
        if (Environment.GetEnvironmentVariable("BALLISTIC_SCREENSHOT_PAUSED") != "1") {
            SceneManager.StartPlay();
        }
        else {
            foreach (Entity entity in SceneManager.GetCurrentScene().Entities)
                if (!entity.IsDestroyed && entity.GetComponent<HDCamera>() is { } camera) {
                    SceneManager.RenderCamera = camera;
                    break;
                }
        }
    }

    public void Run() => loop.Run();
}
