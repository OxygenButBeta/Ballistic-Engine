namespace BallisticEngine;

using System.Threading;

// DECOUPLED RENDER THREAD (BALLISTIC_DX12_RENDER_THREAD=1, default OFF → the proven single-threaded loop).
//
// Sector-standard split (Unreal/Unity model): the MAIN thread stays authoritative — it pumps OS/window events
// (GLFW requires that on the thread that created the window), runs Update (scripts, physics, particles,
// animation, input), and at the end of each Update PUBLISHES a frame snapshot. A dedicated RENDER thread draws
// the PREVIOUS frame's snapshot + presents, in parallel. Frame time becomes max(update, render-submit) instead
// of their sum; the win scales with how heavy EITHER side is (invisible on an empty scene on a fast PC,
// decisive in a real gameplay scene or on a weaker CPU). +1 frame of latency, the standard trade.
//
// Why RENDER is the thread that moves (not game): the game thread owns input + the GLFW event pump (thread-bound)
// and the authoritative simulation — that must stay on main. The render thread owns DX12 frame submission, so it
// calls Dx12Device.BeginFrame()/EndFrame() (the frame-list fast-path keys to the submitting thread) and the
// swapchain Present (DXGI Present is callable from any thread). Game-thread asset uploads then run on a different
// thread than the open frame list, so they take Dx12Device's locked sync-submit path (correct; uploads are rare /
// off the hot frame, so the occasional stall is acceptable).
//
// SYNCHRONISATION (the correctness core): the render thread must NEVER read live, main-thread-mutated state
// (Transform matrices via the lazy WorldMatrix getter, the renderer RuntimeSets, camera/light/volume). The main
// thread publishes a complete FrameSnapshot at the end of Update; the render thread renders only from it. A
// double-buffered hand-off lets the main thread build frame N's snapshot while the render thread draws N-1.
//
// This class owns the THREAD + the hand-off gates only. The snapshot CAPTURE (Transform.PublishWorldForRender,
// the render-set copy, camera/light/volume freeze) is done by the main thread in EngineLoop; the actual DRAW
// (renderCallback) is whatever the host renders — it reads the published snapshot through the same contracts.
public sealed class RenderThread {
    public static bool Enabled { get; } =
        System.Environment.GetEnvironmentVariable("BALLISTIC_DX12_RENDER_THREAD") == "1";

    readonly Thread renderThread;
    readonly System.Action<double> renderCallback;   // draws + presents the published snapshot (runs on render thread)
    volatile bool running;
    Exception renderThreadFault;                      // a render-thread exception is rethrown on the main thread

    // Ping-pong hand-off. The MAIN thread produces a snapshot then signals `snapshotReady`; the RENDER thread
    // waits on it, draws, then signals `renderDone`. The main thread waits on `renderDone` before publishing the
    // NEXT snapshot, so it never overwrites state the render thread is still reading. One frame of overlap: while
    // the render thread draws snapshot N, the main thread is free to run Update N+1 up to the publish point.
    readonly SemaphoreSlim snapshotReady = new(0, 1);
    readonly SemaphoreSlim renderDone = new(1, 1);   // starts signalled: the first publish doesn't wait

    double frameDelta;

    public RenderThread(System.Action<double> renderCallback) {
        this.renderCallback = renderCallback;
        renderThread = new Thread(RenderLoop) { Name = "BallisticRenderThread", IsBackground = false };
    }

    public void Start() {
        running = true;
        renderThread.Start();
    }

    // MAIN thread, called at the END of each frame's Update (after the snapshot has been published into the
    // shared buffers). Releases the render thread to draw the just-published snapshot. Non-blocking: the main
    // thread returns immediately and is free to run the next Update — true overlap. The PREVIOUS frame's draw is
    // synchronised by WaitForRenderIdle() being called BEFORE the next publish (see below).
    public void PublishAndKickRender(double delta) {
        if (renderThreadFault is not null) {
            var f = renderThreadFault; renderThreadFault = null;
            throw new System.Exception("Render thread faulted", f);
        }
        frameDelta = delta;
        snapshotReady.Release();
    }

    // MAIN thread, called BEFORE publishing the next snapshot: block until the render thread has finished drawing
    // the previous one, so the publish can't race the in-flight draw. In steady state the render thread has
    // usually already finished (that's the parallelism); this only blocks when render is slower than update.
    public void WaitForRenderIdle() {
        renderDone.Wait();
    }

    void RenderLoop() {
        try {
            while (running) {
                snapshotReady.Wait();
                if (!running) break;
                renderCallback(frameDelta);
                renderDone.Release();
            }
        }
        catch (Exception e) {
            renderThreadFault = e;
            // Unblock the main thread so it observes the fault instead of hanging on renderDone.
            try { renderDone.Release(); } catch { /* already signalled */ }
        }
    }

    public void Stop() {
        running = false;
        // Wake the render thread if it's parked so it can observe `running == false` and exit.
        try { snapshotReady.Release(); } catch { }
        if (renderThread.IsAlive) renderThread.Join(2000);
    }
}
