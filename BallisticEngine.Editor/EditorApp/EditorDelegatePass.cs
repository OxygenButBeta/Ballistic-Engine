namespace BallisticEngine.Editor;

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
