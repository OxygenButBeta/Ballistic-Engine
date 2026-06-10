namespace BallisticEngine;

// The play-mode frame loop, living in the library so it can drive engine internals
// (timer update, camera render) that are not visible to host exes. Hosts construct it
// and call Run(); the editor drives frames itself and does not use this.
public sealed class EngineLoop {
    readonly IBallisticEngineRuntime runtime;

    public EngineLoop(IBallisticEngineRuntime runtime) {
        this.runtime = runtime;
        runtime.Window.SetFrequency(600);
        runtime.WindowUpdateCallback += Update;
        runtime.WindowRenderCallback += Render;
    }

    void Render(double delta) {
        if (SceneManager.RenderCamera is null) {
            Console.WriteLine("No render camera set in scene.");
            return;
        }

        SceneManager.RenderCamera.RenderCamera();
    }

    void Update(double delta) {
        runtime.EngineTimer.Update(delta);
        SceneManager.Update((float)delta);
    }

    public void Run() => runtime.Window.Run();
}
