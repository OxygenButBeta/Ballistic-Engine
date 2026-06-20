namespace BallisticEngine;

using System.Collections.Generic;

// FRAME SNAPSHOT for the decoupled render thread (BALLISTIC_DX12_RENDER_THREAD=1).
//
// The render thread must read a CONSISTENT, FROZEN view of the frame while the game (main) thread is already
// running the next Update. This class is the hand-off surface: the game thread calls PublishFromGameThread()
// at the end of Update to freeze everything the render path reads; the render thread brackets its draw with
// BeginRenderThreadFrame()/EndRenderThreadFrame().
//
// WHAT IS FROZEN (the state the render path reads that the game thread mutates):
//  1. Per-renderer WORLD MATRICES — the biggest hazard: WorldMatrix is a LAZY getter that recomputes + walks
//     the parent chain + bumps version stamps, so the render thread calling it would both race the game-thread
//     setters AND mutate shared cache. The game thread instead calls Transform.PublishWorldForRender() on every
//     renderer's transform here, copying the final world matrix into a render-thread-only field that the render
//     path reads via Transform.RenderMatrix.
//  2. The RENDER-SET MEMBERSHIP — RuntimeSet<IStaticMeshRenderer> et al. are plain HashSets mutated by
//     OnAttach/OnDetach/spawn/destroy on the game thread. The render path iterates them. We snapshot the
//     membership into a stable list the render thread iterates, so a mid-frame spawn/destroy can't corrupt the
//     iteration. (Per-renderer fields like IsActive are read live — they're simple bools, torn reads are
//     harmless here, and the snapshot already pins WHICH renderers exist.)
//
// Camera/light/volume state is still read live in this first cut: they are small, change rarely, and a one-frame
// torn read is imperceptible (a follow-up can freeze them too). The DESIGN keeps the freeze centralised here.
public static class FrameSnapshot {
    // The render-set snapshot: a stable copy of the renderers the render thread iterates. Double-buffered so the
    // game thread can fill the next frame's copy while the render thread reads this one — but the ping-pong gate
    // in RenderThread already guarantees the render thread is idle before the next publish, so a single reused
    // list is safe and allocation-free (the gate is the mutual-exclusion).
    static readonly List<IStaticMeshRenderer> renderSetSnapshot = new(512);

    // Exposed to the renderer: when the render thread is active, iterate THIS instead of the live RuntimeSet.
    public static IReadOnlyList<IStaticMeshRenderer> RenderSet => renderSetSnapshot;

    // True only between BeginRenderThreadFrame and EndRenderThreadFrame — lets the renderer choose the snapshot
    // path over the live RuntimeSet without a separate flag per call site.
    [System.ThreadStatic] static bool onRenderThread;
    public static bool IsRenderThreadDrawing => onRenderThread;

    // GAME THREAD, end of Update: freeze the world into the snapshot the render thread will read.
    public static void PublishFromGameThread() {
        // 1. Publish every active renderer's world matrix into its render-thread field, and snapshot membership.
        renderSetSnapshot.Clear();
        foreach (IStaticMeshRenderer r in RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection) {
            if (r is null) continue;
            r.Transform?.PublishWorldForRender();   // freeze world matrix on the game thread (safe cache touch)
            renderSetSnapshot.Add(r);
        }
        // The camera transform is read by the render thread too (view matrix) — freeze it as well.
        SceneManager.RenderCamera?.transform?.PublishWorldForRender();
    }

    // RENDER THREAD: mark the draw region so the renderer reads the snapshot, not the live sets.
    public static void BeginRenderThreadFrame() => onRenderThread = true;
    public static void EndRenderThreadFrame() => onRenderThread = false;
}
