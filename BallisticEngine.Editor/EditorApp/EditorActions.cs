using Keys = OpenTK.Windowing.GraphicsLibraryFramework.Keys;

namespace BallisticEngine.Editor;

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
