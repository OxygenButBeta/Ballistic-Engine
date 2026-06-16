using BallisticEngine.AssetPipeline;

namespace BallisticEngine;

// Standalone player host: brings the engine up, loads the startup scene, then runs play mode.
public sealed class BEngineEntry {
    readonly EngineLoop loop;

    public BEngineEntry(IBallisticEngineRuntime runtime, string projectPath, bool playerMode = false) {
        EngineBootstrap bootstrap = new(runtime, projectPath, playerMode: playerMode);

        // A player must not run a project whose game assembly doesn't build (the editor blocks
        // play mode the same way). The compiler errors are already in the log above; exit
        // non-zero so scripted/CI invocations see the failure.
        if (GameScripts.CompileFailed) {
            Debugging.LogError("Game scripts failed to compile — the player will not run. " +
                               "Fix the errors above and start again.");
            Environment.Exit(1);
        }

        loop = new EngineLoop(runtime);

        // Build the scene in edit mode, then enter play (fires component lifecycle).
        // BALLISTIC_SCREENSHOT_PAUSED=1 stays in edit mode: no scripts, no physics, the camera
        // exactly as serialized — deterministic frames for agent/CI screenshot comparison
        // (gameplay sim time at a fixed frame number varies run to run, play-mode shots don't diff).
        // BALLISTIC_SCENE=<project-relative .scene path> loads that scene instead of the startup
        // scene (agents verify ANY scene without editing project.json; pairs with the screenshot/
        // idmap harness — bal render uses it for orbit captures via Library/Temp scene copies).
        string sceneOverride = Environment.GetEnvironmentVariable("BALLISTIC_SCENE");
        if (string.IsNullOrEmpty(sceneOverride))
            bootstrap.LoadStartupScene();
        else
            SceneManager.SceneLoader!.Invoke(sceneOverride.Replace('\\', '/'));
        if (Environment.GetEnvironmentVariable("BALLISTIC_SCREENSHOT_PAUSED") != "1") {
            SceneManager.StartPlay();
        }
        else {
            // Edit mode never fires OnEnabled, so the scene camera must be registered by hand.
            foreach (Entity entity in SceneManager.GetCurrentScene().Entities)
                if (!entity.IsDestroyed && entity.GetComponent<HDCamera>() is { } camera) {
                    SceneManager.RenderCamera = camera;
                    break;
                }
        }
    }

    public void Run() => loop.Run();
}
