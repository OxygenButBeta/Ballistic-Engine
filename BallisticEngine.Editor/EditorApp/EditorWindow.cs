namespace BallisticEngine.Editor;

public abstract class EditorWindow {
    public string DockKey { get; protected set; }
    public string Title { get; protected set; }
    public string Icon { get; protected set; }

    public bool IsViewport { get; protected set; }

    public bool Singleton { get; protected set; } = true;

    public Vector2 DesiredSize { get; protected set; } = new(420, 540);

    public bool Open;

    public bool NoCollapse { get; protected set; }

    public virtual void OnEnable() { }
    public virtual void OnDisable() { }

    protected abstract void OnGui(IEditorGui gui);

    internal void Frame(IEditorGui gui) => OnGui(gui);

    internal void ConfigureFromMeta(string key, EditorWindowMetaAttribute meta) {
        DockKey ??= key;
        Title ??= meta.Title;
        Icon ??= meta.Icon;
        if (DesiredSize == new Vector2(420, 540)) DesiredSize = new Vector2(meta.Width, meta.Height);
    }

    public void DrawStandalone(IEditorGui gui) {
        if (!Open) return;
        bool open = Open;
        WindowShell.DrawStandalone(this, gui, ref open);
        Open = open;
    }
}
