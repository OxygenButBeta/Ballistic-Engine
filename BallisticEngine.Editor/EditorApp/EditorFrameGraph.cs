namespace BallisticEngine.Editor;

public sealed class EditorFrameGraph {
    readonly OrderedPassList<IEditorFramePass> passes = new(p => (int)p.Event);

    public EditorFrameGraph Add(IEditorFramePass pass) { passes.Add(pass); return this; }

    public void Build() => passes.Build();

    public System.Collections.Generic.IReadOnlyList<IEditorFramePass> Passes => passes.Passes;

    public void Execute(EditorFrameContext ctx) => passes.Execute(p => p.Run(ctx));
}
