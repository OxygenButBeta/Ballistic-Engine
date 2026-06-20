using System.Numerics;

namespace BallisticEngine.Editor;

// The Unity-style base every editor window/panel derives from. Identity (dock key, title, icon) and
// lifecycle (OnEnable/OnDisable/OnGui) live here; the window's Begin/End, focus and maximize are owned
// by WindowShell so a derived class NEVER touches ImGui windowing — it only fills OnGui(IEditorGui).
//
// DockKey is the stable identity: it equals an EditorLayout.* name AND the ImGui "###id", so the dock
// layout .ini, the dock-builder targets and the persisted hidden-set all key on the same string
// (the EF12 contract — changing it breaks saved layouts).
public abstract class EditorWindow {
    public string DockKey { get; protected set; }
    public string Title { get; protected set; }
    public string Icon { get; protected set; }

    // Scene/Game views: special render-target compositing instead of a generic body. A viewport window
    // has no OnGui body (WindowShell skips it; EditorApplication composites it via DrawViewportWindows).
    public bool IsViewport { get; protected set; }

    // false => DockPanelHost may open additional independent instances ("Add Tab"); true => one instance.
    public bool Singleton { get; protected set; } = true;

    // Default floating size used the first time the window is shown (FirstUseEver).
    public Vector2 DesiredSize { get; protected set; } = new(420, 540);

    // Standalone (floating, Window-menu-toggled) windows own their own show state here — the menu's
    // Toggle/Open/checkmark flips this, and DrawStandalone routes the body through WindowShell with it.
    // Docked CORE panels do NOT use this: their show state is owned by EditorPanelRegistry.Descriptor.Shown.
    public bool Open;

    // Standalone windows that want a NoCollapse title bar (Settings / Tags & Layers / matrix) set this.
    public bool NoCollapse { get; protected set; }

    // Shown (focus-opened) / hidden (closed via the X). Override to allocate/release per-open resources.
    public virtual void OnEnable() { }
    public virtual void OnDisable() { }

    // The ONLY required override: the window body, drawn through the seam. No ImGui import in subclasses.
    protected abstract void OnGui(IEditorGui gui);

    // WindowShell calls this between Begin and End (only when the window is visible).
    internal void Frame(IEditorGui gui) => OnGui(gui);

    // UserEditorWindowRegistry calls this on a freshly-instantiated [EditorWindowMeta] window so the author
    // doesn't have to wire DockKey/Title/Icon/size in their ctor — the attribute is the single source. Only
    // fills fields the author left unset (a ctor that DID set Title/Icon/size wins).
    internal void ConfigureFromMeta(string key, EditorWindowMetaAttribute meta) {
        DockKey ??= key;
        Title ??= meta.Title;
        Icon ??= meta.Icon;
        if (DesiredSize == new Vector2(420, 540))   // still the base default → take the attribute's size
            DesiredSize = new Vector2(meta.Width, meta.Height);
    }

    // Draw this as a standalone floating window when Open: routes through WindowShell (the single Begin/End
    // owner) with the panel-owned Open flag. A no-op while closed. Used by the Window-menu-toggled panels
    // (Settings / Tags & Layers / Layer Collision Matrix / Profiler) — NOT the docked core panels.
    public void DrawStandalone(IEditorGui gui) {
        if (!Open) return;
        bool open = Open;
        WindowShell.DrawStandalone(this, gui, ref open);
        Open = open;
    }
}
