namespace BallisticEngine.Editor;

// A2 (editor-rework): the editor frame loop's executor — a thin editor-side wrapper over the engine-side,
// headless-tested OrderedPassList<T> (the R1 stable-order substrate). It registers the IEditorFramePass list
// ONCE, freezes the order (ascending EditorFramePassEvent, stable tie-break = registration order), and runs
// each pass per frame against the shared EditorFrameContext.
//
// Mirrors Dx12RenderGraph's role for the renderer: the ordering/stability logic lives in the shared substrate
// (OrderedPassList, tested in BallisticEngine.Tests.Reflection), so this class is just the editor binding —
// it adds the pass type + the per-frame context, nothing load-bearing.
public sealed class EditorFrameGraph {
    readonly OrderedPassList<IEditorFramePass> passes = new(p => (int)p.Event);

    public EditorFrameGraph Add(IEditorFramePass pass) { passes.Add(pass); return this; }

    // Freeze the order. Optional — Execute builds lazily — but called once after registration for clarity.
    public void Build() => passes.Build();

    public System.Collections.Generic.IReadOnlyList<IEditorFramePass> Passes => passes.Passes;

    // Run the whole frame: every pass in the frozen order, applied to this frame's context.
    public void Execute(EditorFrameContext ctx) => passes.Execute(p => p.Run(ctx));
}

// The concrete passes. Each is a tiny adapter that delegates to a private EditorApplication method holding the
// VERBATIM old OnRender body slice — keeping the move a pure structural change (the bodies are unchanged; only
// their call site moved from one method into named, ordered units). A delegate-backed pass (rather than one
// class per body) keeps the diff minimal and the bodies co-located with the state they touch.
internal sealed class EditorDelegatePass : IEditorFramePass {
    readonly System.Action<EditorFrameContext> run;
    public EditorDelegatePass(EditorFramePassEvent ev, string name, System.Action<EditorFrameContext> run) {
        Event = ev;
        Name = name;
        this.run = run;
    }
    public EditorFramePassEvent Event { get; }
    public string Name { get; }
    public void Run(EditorFrameContext ctx) => run(ctx);
}
