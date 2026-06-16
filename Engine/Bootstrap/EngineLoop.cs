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

        // After the scene renders: resume any `await Coroutine.EndOfFrame()` continuations.
        Coroutine.EndOfFramePump();
    }

    void Update(double delta) {
        // Drop last frame's single-frame debug lines before this frame's Tick repopulates them.
        // No-op in a release player (DebugDraw.Enabled is false) — only matters if a game turns it on.
        DebugDraw.Expire();

        runtime.EngineTimer.Update(delta);
        SceneManager.Update((float)delta);

        // Step particle systems once per frame (after Update so emitter transforms are current).
        // Driven here, not from a Behaviour Tick, so it advances exactly once regardless of render count.
        ParticleSystem.AdvanceAll((float)delta);
        TrailRenderer.AdvanceAll((float)delta);   // lay/age trail points after transforms update

        // Push the listener pose set by AudioListener.Tick this frame and recycle finished voices.
        // After SceneManager.Update so the listener/emitter transforms are current.
        Audio.Update();

        // Snapshot this frame's action down-state for next frame's GetButtonDown/Up edges. AFTER
        // scripts Tick so a press is reported exactly one frame, Unity-style.
        InputActions.Update();

        // Standalone player: the whole window IS the game, so the script's cursor intent always
        // applies. (The editor resolves intent itself, with a focus veto — see EditorApplication.)
        Cursor.Apply(allowed: true);
    }

    public void Run() => runtime.Window.Run();
}
