namespace BallisticEngine.Editor;

internal sealed class LegacyWindow : EditorWindow {
    readonly System.Action drawBody;

    public LegacyWindow(string dockKey, string title, string icon, System.Action drawBody,
        bool isViewport = false) {
        DockKey = dockKey;
        Title = title;
        Icon = icon;
        IsViewport = isViewport;
        this.drawBody = drawBody;
    }

    protected override void OnGui(IEditorGui gui) => drawBody?.Invoke();
}
