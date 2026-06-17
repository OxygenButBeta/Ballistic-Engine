namespace BallisticEngine.Editor;

// A2 (editor-rework): the editor frame loop expressed as a declared, ordered list of named passes instead of
// one ~100-line OnRender method. This mirrors EXACTLY what the renderer did to DX12HDRenderer (an IRenderPass
// list ordered by a Dx12RenderPassEvent) — here IEditorFramePass ordered by EditorFramePassEvent, executed by
// EditorFrameGraph (which rides the engine-side OrderedPassList<T> substrate, the headless-tested R1 core).
//
// This is a PURE STRUCTURAL MOVE: each pass's Run body is a VERBATIM slice of the old OnRender, in the EXACT
// same order (the events are numbered to reproduce it). The point is legibility + injectability, not behavior
// change — the events make the load-bearing "UI build → scene render → present" order explicit instead of
// implicit in one method body.

// WHEN a pass runs within the editor frame. The MEMBER ORDER is the frame order; values are spaced by 50 so a
// future pass (e.g. an overlay, a perception capture) can slot at Event+1 without renumbering. Reproduces the
// old OnRender sequence exactly:
//   ImportPump ........ AsyncAssetImport.PumpCompletion (finish background-import main-thread work)
//   RemotePump ........ RemoteCommandQueue.Pump (agent/MCP commands, before the UI builds)
//   BuildUI ........... imgui.Update + BuildUI + BusyOverlay  (the gizmo mutates transforms HERE)
//   StartupImport ..... kick the one-time startup asset import on the first painted frame
//   ResolveDirty ...... decide whether to re-render the scene this frame (viewport-dirty / camera move / etc.)
//   ViewportRender .... render the active Scene/Game view offscreen (consumes ctx.RenderScene)
//   ImGuiRender ....... imgui.Render (records the ImGui draw data; present happens after OnRender returns)
//   PostPresent ....... deferred scene-open pump + CPU-ms EMA
//   IdleThrottle ...... drop to the idle FPS cap when nothing is happening
public enum EditorFramePassEvent {
    ImportPump      = 0,
    RemotePump      = 50,
    BuildUI         = 100,
    StartupImport   = 150,
    ResolveDirty    = 200,
    ViewportRender  = 250,
    ImGuiRender     = 300,
    PostPresent     = 350,
    IdleThrottle    = 400,
}

// Per-frame state threaded through the passes. Carries the frame delta plus the ONE cross-pass local the old
// method computed mid-body (RenderScene): ResolveDirty decides it, ViewportRender consumes it, IdleThrottle
// reads it. Everything else the passes touch is EditorApplication instance state (forceFrames, frameWatch,
// lastCameraMatrix, ...), reached directly because the passes are owned by EditorApplication — so this context
// stays tiny, exactly the locals that used to flow between statements in OnRender.
public sealed class EditorFrameContext {
    public double Delta;
    public bool RenderScene;   // set by ResolveDirty, consumed by ViewportRender + IdleThrottle
}

// A pluggable editor frame pass. Event = when it runs (sorted, stable tie-break = registration order). Name =
// for future per-pass profiling/inspection (the renderer's TimePass analogue). Run = the verbatim body slice.
public interface IEditorFramePass {
    EditorFramePassEvent Event { get; }
    string Name { get; }
    void Run(EditorFrameContext ctx);
}
