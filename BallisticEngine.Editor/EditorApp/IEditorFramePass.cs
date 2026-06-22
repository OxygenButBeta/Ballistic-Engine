namespace BallisticEngine.Editor;

public interface IEditorFramePass {
    EditorFramePassEvent Event { get; }
    string Name { get; }
    void Run(EditorFrameContext ctx);
}
