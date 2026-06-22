namespace BallisticEngine;

public sealed class EngineLoop {
    readonly IBallisticEngineRuntime runtime;
    readonly RenderThread renderThread;

    public EngineLoop(IBallisticEngineRuntime runtime) {
        this.runtime = runtime;
        runtime.Window.SetFrequency(600);

        if (RenderThread.Enabled) {
            renderThread = new RenderThread(RenderOnRenderThread);
            runtime.WindowUpdateCallback += UpdateAndDispatch;
            renderThread.Start();
        }
        else {
            runtime.WindowUpdateCallback += Update;
            runtime.WindowRenderCallback += Render;
        }
    }

    void Render(double delta) {
        if (SceneManager.RenderCamera is null) {
            Console.WriteLine("No render camera set in scene.");
            return;
        }

        SceneManager.RenderCamera.RenderCamera();

        Coroutine.EndOfFramePump();
    }

    void Update(double delta) {
        DebugDraw.Expire();

        runtime.EngineTimer.Update(delta);
        SceneManager.Update((float)delta);

        ParticleSystem.AdvanceAll((float)delta);
        TrailRenderer.AdvanceAll((float)delta);

        Audio.Update();

        InputActions.Update();

        Cursor.Apply(allowed: true);
    }

    void UpdateAndDispatch(double delta) {
        renderThread.WaitForRenderIdle();

        Update(delta);

        FrameSnapshot.PublishFromGameThread();

        renderThread.PublishAndKickRender(delta);
    }

    void RenderOnRenderThread(double delta) {
        if (SceneManager.RenderCamera is null) return;
        FrameSnapshot.BeginRenderThreadFrame();
        SceneManager.RenderCamera.RenderCamera();
        Coroutine.EndOfFramePump();
        runtime.PresentFromRenderThread();
        FrameSnapshot.EndRenderThreadFrame();
    }

    public void Run() => runtime.Window.Run();

    public void Shutdown() => renderThread?.Stop();
}
