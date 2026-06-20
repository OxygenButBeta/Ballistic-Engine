using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;

namespace BallisticEngine.Editor;

// The ONE place an editor window's ImGui Begin/End is paired. Centralising it here means the hard-won
// invariant — "End() is always called once Begin() ran, even when Begin returns false or the close
// button just flipped `shown` this frame" (the old DrawDockPanel contract) — exists in a single spot,
// and every window body (EditorWindow.OnGui) runs through the same shell. This is a SEAM component, so
// importing ImGui here is the allowed boundary (a window body still never sees ImGui).
//
// `titleStrip` is the maximize-on-title-double-click handler (EditorApplication owns the geometry); the
// shell calls it right after a visible Begin, exactly where DrawDockPanel did. `requestFocus` surfaces
// a window re-opened from the Window menu (Unity-style focus-on-open).
internal static class WindowShell {
    // Draw a docked/floating window. `shown` is by ref so the close-button X writes back through it,
    // exactly like the registry's DrawCore expects. The window is skipped entirely if it's a viewport
    // (those composite their render target via a different path).
    public static void Draw(EditorWindow win, IEditorGui gui, ref bool shown,
                            bool requestFocus, System.Action<string> titleStrip) {
        if (win.IsViewport)
            return;

        if (requestFocus)
            ImGui.SetNextWindowFocus();

        ImGui.SetNextWindowSize(win.DesiredSize, ImGuiCond.FirstUseEver);

        // Display Title with the DockKey as the ###id (stable dock identity; see EditorWindow.DockKey).
        string label = $"{win.Title}###{win.DockKey}";
        bool visible = ImGui.Begin(label, ref shown);
        if (visible) {
            titleStrip?.Invoke(win.DockKey);
            win.Frame(gui);
        }
        ImGui.End();
    }

    // Draw a window filling a given rect (the maximize path). Returns the (possibly close-button-flipped)
    // shown state so the caller persists it. Mirrors the docked path's End-always pairing.
    //
    // EF9b (doesn't-fight-docking): the maximized window gets its OWN dedicated `###maxpanel` id with
    // NoSavedSettings — NOT the docked `###DockKey` — so ImGui doesn't force-undock the panel or write the
    // fullscreen geometry into the docked window's saved settings. `titleStrip` still gets the real DockKey
    // (the maximize KEY) so the title double-click restores the right panel. Content is the SAME OnGui, so
    // docked and maximized views share one instance/state.
    public static bool DrawMaximized(EditorWindow win, IEditorGui gui,
                                     SysVec2 pos, SysVec2 size, System.Action<string> titleStrip) {
        if (win.IsViewport)
            return true;

        ImGui.SetNextWindowPos(pos);
        ImGui.SetNextWindowSize(size);

        bool shown = true;
        // Visible title from the window; fixed `###maxpanel` id (dedicated fullscreen identity).
        string label = $"{win.Icon}  {win.Title}###maxpanel";
        bool visible = ImGui.Begin(label, ref shown,
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings);
        if (visible) {
            titleStrip?.Invoke(win.DockKey);
            win.Frame(gui);
        }
        ImGui.End();
        return shown;
    }
}
