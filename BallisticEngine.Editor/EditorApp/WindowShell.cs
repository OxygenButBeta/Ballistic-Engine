using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;

namespace BallisticEngine.Editor;

internal static class WindowShell {
    public static void Draw(EditorWindow win, IEditorGui gui, ref bool shown,
                            bool requestFocus, System.Action<string> titleStrip) {
        if (win.IsViewport)
            return;

        if (requestFocus)
            ImGui.SetNextWindowFocus();

        ImGui.SetNextWindowSize(win.DesiredSize, ImGuiCond.FirstUseEver);

        string label = $"{win.Title}###{win.DockKey}";
        bool visible = ImGui.Begin(label, ref shown);
        if (visible) {
            titleStrip?.Invoke(win.DockKey);
            win.Frame(gui);
        }
        ImGui.End();
    }

    public static void DrawStandalone(EditorWindow win, IEditorGui gui, ref bool shown) {
        if (win.IsViewport)
            return;

        ImGui.SetNextWindowSize(win.DesiredSize * gui.Scale, ImGuiCond.FirstUseEver);
        ImGuiWindowFlags flags = win.NoCollapse ? ImGuiWindowFlags.NoCollapse : ImGuiWindowFlags.None;
        string visibleTitle = string.IsNullOrEmpty(win.Icon) ? win.Title : $"{win.Icon}  {win.Title}";
        string label = $"{visibleTitle}###{win.DockKey}";
        bool visible = ImGui.Begin(label, ref shown, flags);
        if (visible)
            win.Frame(gui);
        ImGui.End();
    }

    public static bool DrawMaximized(EditorWindow win, IEditorGui gui,
                                     SysVec2 pos, SysVec2 size, System.Action<string> titleStrip) {
        if (win.IsViewport)
            return true;

        ImGui.SetNextWindowPos(pos);
        ImGui.SetNextWindowSize(size);

        bool shown = true;
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
