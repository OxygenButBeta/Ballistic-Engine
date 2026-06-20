namespace BallisticEngine;

// The play-mode frame loop, living in the library so it can drive engine internals
// (timer update, camera render) that are not visible to host exes. Hosts construct it
// and call Run(); the editor drives frames itself and does not use this.
//
// THREADING: by default Update + Render run sequentially on the window thread (the proven path). When
// BALLISTIC_DX12_RENDER_THREAD=1, render is moved to a dedicated thread (see RenderThread): the window
// thread runs Update + publishes a frame snapshot; the render thread draws the PREVIOUS snapshot + presents
// in parallel. Frame time becomes max(update, render) instead of their sum.
public sealed class EngineLoop {
    readonly IBallisticEngineRuntime runtime;
    readonly RenderThread renderThread;   // null when the decoupled render thread is off (the default)

    public EngineLoop(IBallisticEngineRuntime runtime) {
        this.runtime = runtime;
        runtime.Window.SetFrequency(600);

        if (RenderThread.Enabled) {
            // Decoupled: the window thread drives Update; the render thread draws. We DON'T subscribe the
            // render to WindowRenderCallback — OnUpdateFrame drives everything (Update, snapshot publish, and
            // kicking the render thread). The host's present is invoked from the render thread via Render().
            renderThread = new RenderThread(RenderOnRenderThread);
            runtime.WindowUpdateCallback += UpdateAndDispatch;
            renderThread.Start();
        }
        else {
            runtime.WindowUpdateCallback += Update;
            runtime.WindowRenderCallback += Render;
        }
    }

    // ---- single-threaded path (default) ----

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

    // ---- decoupled render-thread path (BALLISTIC_DX12_RENDER_THREAD=1) ----

    // Runs on the WINDOW thread (OpenTK OnUpdateFrame). Update the simulation, freeze it into a render
    // snapshot, then hand that snapshot to the render thread and let it draw in parallel while this thread
    // returns to pump events + run the next Update.
    void UpdateAndDispatch(double delta) {
        // Make sure the render thread has finished drawing the PREVIOUS snapshot before we run an Update that
        // mutates the live state it was reading. In steady state it's already done (that's the overlap); this
        // only blocks when render is the slower side.
        renderThread.WaitForRenderIdle();

        Update(delta);

        // Freeze the just-updated world into the snapshot the render thread will read (Transform world matrices,
        // render-set copy, camera/light/volume). Done on THIS (the game) thread so it touches the live lazy
        // caches safely; the render thread then only reads frozen data.
        FrameSnapshot.PublishFromGameThread();

        // Kick the render thread to draw the snapshot we just published. Non-blocking — we return to OnUpdateFrame,
        // OpenTK pumps events, and the next Update overlaps this frame's draw.
        renderThread.PublishAndKickRender(delta);
    }

    // Runs on the RENDER thread. Draws the published snapshot + presents. Reads ONLY frozen snapshot state.
    void RenderOnRenderThread(double delta) {
        if (SceneManager.RenderCamera is null) return;
        FrameSnapshot.BeginRenderThreadFrame();
        SceneManager.RenderCamera.RenderCamera();
        Coroutine.EndOfFramePump();   // EndOfFrame continuations resume after the draw (same as single-threaded)
        runtime.PresentFromRenderThread();   // the host blits the renderer's LDR target + flips
        FrameSnapshot.EndRenderThreadFrame();
    }

    public void Run() => runtime.Window.Run();

    // Called by the host when the window loop returns, so the render thread is joined before teardown.
    public void Shutdown() => renderThread?.Stop();
}
