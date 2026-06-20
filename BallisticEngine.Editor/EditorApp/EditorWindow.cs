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
    public bool IsViewport { get; protected init; }

    // false => DockPanelHost may open additional independent instances ("Add Tab"); true => one instance.
    public bool Singleton { get; protected init; } = true;

    // Default floating size used the first time the window is shown (FirstUseEver).
    public Vector2 DesiredSize { get; protected set; } = new(420, 540);

    // Shown (focus-opened) / hidden (closed via the X). Override to allocate/release per-open resources.
    public virtual void OnEnable() { }
    public virtual void OnDisable() { }

    // The ONLY required override: the window body, drawn through the seam. No ImGui import in subclasses.
    protected abstract void OnGui(IEditorGui gui);

    // WindowShell calls this between Begin and End (only when the window is visible).
    internal void Frame(IEditorGui gui) => OnGui(gui);
}
