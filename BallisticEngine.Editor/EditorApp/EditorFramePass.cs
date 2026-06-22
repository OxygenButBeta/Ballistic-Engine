namespace BallisticEngine.Editor;

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

public sealed class EditorFrameContext {
    public double Delta;
    public bool RenderScene;
}

public interface IEditorFramePass {
    EditorFramePassEvent Event { get; }
    string Name { get; }
    void Run(EditorFrameContext ctx);
}
