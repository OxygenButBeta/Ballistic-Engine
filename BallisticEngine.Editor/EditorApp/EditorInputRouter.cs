using Keys = OpenTK.Windowing.GraphicsLibraryFramework.Keys;

namespace BallisticEngine.Editor;

[Flags]
internal enum EditorInputContext {
    None            = 0,
    Global          = 1 << 0,
    SceneView       = 1 << 1,
    SceneViewHovered = 1 << 2,
    GameView        = 1 << 3,
}

internal static class EditorActions {
    public const string Undo          = "edit.undo";
    public const string Redo          = "edit.redo";
    public const string Save          = "file.save";
    public const string RebuildScripts = "scripts.rebuild";
    public const string ExitMaximize  = "view.exitMaximize";
    public const string GizmoTranslate = "gizmo.translate";
    public const string GizmoRotate    = "gizmo.rotate";
    public const string GizmoScale     = "gizmo.scale";
    public const string FrameSelected  = "scene.frameSelected";
    public const string AlignToView    = "scene.alignToView";
    public const string CopyEntity      = "scene.copyEntity";
    public const string PasteEntity     = "scene.pasteEntity";
}

internal sealed class EditorInputRouter {
    readonly InputActionChain<Keys> chain = new();
    readonly EditorInput input;

    public Func<string, bool> ActionEnabled;

    public EditorInputRouter(EditorInput input) =>
        this.input = input ?? throw new ArgumentNullException(nameof(input));

    public EditorInputRouter Bind(string id, KeyChord<Keys> chord, EditorInputContext context, Action invoke,
                                  int priority = 0) {
        chain.Add(id, chord, (int)context, priority, invoke);
        return this;
    }

    public void Build() => chain.Build();

    public bool Dispatch(EditorInputContext liveContexts) =>
        chain.Dispatch((int)liveContexts, IsChordActive, GateFor);

    bool GateFor(InputAction<Keys> a) => ActionEnabled is null || ActionEnabled(a.Id);

    bool IsChordActive(KeyChord<Keys> chord) =>
        input.KeyPressed(chord.Key) &&
        input.CtrlDown == chord.Ctrl &&
        input.ShiftDown == chord.Shift;

    public System.Collections.Generic.IReadOnlyList<InputConflict> Conflicts() => chain.CheckConflicts();
}
