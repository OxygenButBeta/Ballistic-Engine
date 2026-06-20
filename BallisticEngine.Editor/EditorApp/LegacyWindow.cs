namespace BallisticEngine.Editor;

// Phase-3 bridge: wraps a not-yet-migrated panel (one that still draws with raw ImGui via an
// `Action drawContents`) in the EditorWindow base, so the registry / WindowShell / maximize paths can
// treat EVERY core panel uniformly as an EditorWindow. Its OnGui ignores the seam `gui` and calls the
// legacy body directly — the body keeps using ImGui, which is allowed inside the editor.
//
// As each panel is ported to derive from EditorWindow itself (filling OnGui through IEditorGui), its
// LegacyWindow wrapper is removed and the real subclass is registered instead. When the last panel is
// ported this whole class is deleted.
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

    // The legacy body still calls ImGui directly; the seam `gui` is intentionally unused here.
    protected override void OnGui(IEditorGui gui) => drawBody?.Invoke();
}
