namespace BallisticEngine.Editor;

// A1b (single-sourced maximize) → A1b-deeper (single-OWNED core panels). The ONE place each core
// dockable panel is declared AND the single owner of its show-state. Before A1b a core panel was spread
// across non-contiguous sites that had to agree by hand:
//   (1) the normal draw call          (BuildUI: `if (showInspector) DrawDockPanel(Inspector, ...)`)
//   (2) the maximize CONTENT re-route (DrawMaximizedPanel's `if (name == Inspector) inspector.Draw...`)
//   (3) the still-available hand-list (MaximizedPanelStillAvailable's `if (name == Inspector) return showInspector`)
// A1b collapsed (1)/(2)/(3) into a descriptor; A1b-deeper goes further and makes the registry OWN the
// `bool Shown` visibility too (was the five `showXxx` fields on EditorApplication, each switched-by-name
// in five more sites: Toggle / Open / IsWindowOpen / Add-Tab / Reset). Now there is NO core panel named
// by EditorApplication: the menu toggles by KEY, the draw loop walks the descriptors, and the maximize
// paths read the same entry. Adding a core panel = ONE Register() call (the Phase-A end-state).
//
// It is a thin data table (key -> descriptor) — NOT a duplicate-instance host (that's DockPanelHost,
// for the "Add Tab" extras). The descriptors hold delegates so the registry itself takes no ImGui
// dependency; EditorApplication owns the ImGui Begin/End and the geometric tab hit-test (it draws each
// core panel via DrawCore's callback, which writes the close-button result back into Shown).
internal sealed class EditorPanelRegistry {
    internal sealed class Descriptor {
        public string Key;                  // the EditorLayout.* dock name (also the ImGui ### id + maximize key)
        public string Title;
        public string Icon;
        public EditorWindow Window;         // Phase 3: the window this descriptor draws (a real subclass or a LegacyWindow bridge). Null for viewports.
        public bool IsViewport;             // Scene/Game views: special render-target compositing, not a generic body
        public bool Shown = true;           // current open state (owned here — replaces the showXxx fields). Viewports ignore it.
    }

    readonly Dictionary<string, Descriptor> byKey = new();
    readonly List<Descriptor> ordered = new();   // declaration order (stable; mirrors the old hardcoded draw order)

    public IReadOnlyList<Descriptor> All => ordered;

    // Phase-3 registration: a core panel is declared by the EditorWindow that draws it. Viewports pass
    // a null window (they composite their render target via DrawViewportWindows). Title/Icon/Key are
    // taken from the window when present (single source), falling back to the args for viewports.
    public void Register(EditorWindow window, string key, string title, string icon,
        bool isViewport = false) {
        var d = new Descriptor {
            Key = key, Title = title, Icon = icon,
            Window = window, IsViewport = isViewport,
        };
        byKey[key] = d;
        ordered.Add(d);
    }

    // Back-compat overload for not-yet-migrated panels: wrap the raw-ImGui body in a LegacyWindow so the
    // descriptor still carries an EditorWindow. Viewports pass a null body (isViewport: true).
    public void Register(string key, string title, string icon, System.Action drawContents,
        bool isViewport = false) {
        EditorWindow win = isViewport ? null : new LegacyWindow(key, title, icon, drawContents);
        Register(win, key, title, icon, isViewport);
    }

    public Descriptor Get(string key) => byKey.TryGetValue(key, out Descriptor d) ? d : null;

    public bool Contains(string key) => byKey.ContainsKey(key);

    // Is this a NON-viewport core panel (has a generic body the docked + maximize paths draw)?
    public bool IsCorePanel(string key) => Get(key) is { IsViewport: false };

    // Current open state of a registered panel (false for an unknown key). Viewports are always shown.
    public bool IsShown(string key) {
        Descriptor d = Get(key);
        if (d is null) return false;
        return d.IsViewport || d.Shown;
    }

    // Window-menu checkbox behaviour: flip a core panel's show-state and return the NEW state (so the
    // caller can request focus when it just opened, exactly as the old per-name switch did). No-op +
    // false for a viewport / unknown key.
    public bool Toggle(string key) {
        if (Get(key) is not { IsViewport: false } d) return false;
        d.Shown = !d.Shown;
        return d.Shown;
    }

    // Re-show a hidden core panel. Returns true if it was hidden and is now shown — the caller
    // (OpenWindow / Add-Tab) opens an EXTRA host instance instead when the primary was already visible.
    public bool Show(string key) {
        if (Get(key) is not { IsViewport: false } d) return false;
        if (d.Shown) return false;
        d.Shown = true;
        return true;
    }

    // Directly set a core panel's show-state (EF9a: the maximized draw path writes back the close-button
    // result here, exactly as DrawCore does for the docked path). No-op for a viewport / unknown key.
    public void SetShown(string key, bool shown) {
        if (Get(key) is { IsViewport: false } d) d.Shown = shown;
    }

    // Reset all core panels to visible (the Reset-Layout default). Viewports are untouched (always shown).
    public void ResetVisibility() {
        foreach (Descriptor d in ordered)
            if (!d.IsViewport) d.Shown = true;
    }

    // EF9c (layout restore): the keys of core panels the user has CLOSED. The dock-layout .ini persists
    // each window's geometry/dock node but NOT whether the editor is currently submitting it, so a closed
    // panel would re-open on the next launch (Shown defaults true) unless we persist this set separately.
    // Viewports are never "closed" (one renderer target) so they're excluded.
    public IEnumerable<string> HiddenKeys() {
        foreach (Descriptor d in ordered)
            if (!d.IsViewport && !d.Shown) yield return d.Key;
    }

    // EF9c (layout restore): re-apply a persisted closed-panel set on startup — every non-viewport core
    // panel is shown unless its key is in `hidden`. Called once after the dock layout loads, before the
    // first frame submits the panels, so a panel the user closed last session stays closed.
    public void ApplyHidden(IReadOnlyCollection<string> hidden) {
        foreach (Descriptor d in ordered)
            if (!d.IsViewport) d.Shown = !hidden.Contains(d.Key);
    }

    // Whether `key` is a registered panel that is currently shown (so it can be drawn fullscreen).
    // Viewports are always available (one renderer target, never "closed"). Unregistered key -> false.
    public bool IsAvailable(string key) => IsShown(key);

    // Phase 3: draw every shown, non-viewport core panel in declaration order THROUGH WindowShell, so the
    // single Begin/End-pairing invariant lives in one place and every panel is an EditorWindow. The
    // close-button X writes back through `ref shown` into the descriptor. `requestFocus(key)` surfaces a
    // just-reopened panel (Unity focus-on-open); `titleStrip(key)` runs the maximize/Add-Tab tab handler
    // right after a visible Begin. Viewports are excluded (their compositing path is DrawViewportWindows).
    public void DrawCore(IEditorGui gui, System.Func<string, bool> requestFocus,
        System.Action<string> titleStrip) {
        foreach (Descriptor d in ordered) {
            if (d.IsViewport || !d.Shown || d.Window is null) continue;
            bool shown = d.Shown;
            WindowShell.Draw(d.Window, gui, ref shown, requestFocus(d.Key), titleStrip);
            d.Shown = shown;   // honour a close-button X that flipped it this frame
        }
    }
}
