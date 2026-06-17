namespace BallisticEngine.Editor;

// A1b (single-sourced maximize). The ONE place each core dockable panel is declared. Before this, a
// core panel was spread across non-contiguous sites that had to agree by hand:
//   (1) the normal draw call          (BuildUI: `if (showInspector) DrawDockPanel(Inspector, ...)`)
//   (2) the maximize CONTENT re-route (DrawMaximizedPanel's `if (name == Inspector) inspector.Draw...`)
//   (3) the still-available hand-list (MaximizedPanelStillAvailable's `if (name == Inspector) return showInspector`)
// A new panel that forgot (2) hit the "can't be shown fullscreen" dead-end; one that forgot (3) got
// STUCK maximized. This registry collapses (1)/(2)/(3) into a SINGLE descriptor per panel: declare it
// once, and the normal path, the maximize path, and the availability check all read the same entry.
//
// It is a thin data table (key -> descriptor) — NOT a duplicate-instance host (that's DockPanelHost,
// for the "Add Tab" extras). The descriptors hold delegates so the registry itself takes no ImGui
// dependency; EditorApplication owns the ImGui Begin/End and the geometric tab hit-test.
internal sealed class EditorPanelRegistry {
    internal sealed class Descriptor {
        public string Key;                  // the EditorLayout.* dock name (also the ImGui ### id + maximize key)
        public string Title;
        public string Icon;
        public System.Action DrawContents;  // draws the panel body (null for viewports — they use the compositing path)
        public System.Func<bool> IsShown;   // whether the panel is currently open (drives maximize availability)
        public bool IsViewport;             // Scene/Game views: special render-target compositing, not a generic body
    }

    readonly Dictionary<string, Descriptor> byKey = new();
    readonly List<Descriptor> ordered = new();   // declaration order (stable; mirrors the old hardcoded draw order)

    public IReadOnlyList<Descriptor> All => ordered;

    public void Register(string key, string title, string icon, System.Action drawContents,
        System.Func<bool> isShown, bool isViewport = false) {
        var d = new Descriptor {
            Key = key, Title = title, Icon = icon,
            DrawContents = drawContents, IsShown = isShown, IsViewport = isViewport,
        };
        byKey[key] = d;
        ordered.Add(d);
    }

    public Descriptor Get(string key) => byKey.TryGetValue(key, out Descriptor d) ? d : null;

    public bool Contains(string key) => byKey.ContainsKey(key);

    // Whether `key` is a registered panel that is currently shown (so it can be drawn fullscreen).
    // Viewports are always available (one renderer target, never "closed"). Unregistered key -> false.
    public bool IsAvailable(string key) {
        Descriptor d = Get(key);
        if (d is null) return false;
        return d.IsViewport || d.IsShown();
    }
}
