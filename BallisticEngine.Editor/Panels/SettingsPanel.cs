using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;

namespace BallisticEngine.Editor;

// Project/editor Settings window (toggled from the Window menu). Edits EditorPrefs.Current live â€”
// every change persists immediately and accent changes re-apply the theme through the supplied
// callback. Open is owned by the caller so the Window menu can toggle it.
internal sealed class SettingsPanel {
    readonly Action<System.Numerics.Vector4> applyAccent;
    readonly Action applyFrameLimit;

    public bool Open;

    // Shared with the toolbar's FPS popup so both edit the same presets.
    public static readonly int[] FrameLimitOptions = [0, 30, 60, 120, 144, 240];
    public static readonly string[] FrameLimitLabels = ["VSync", "30", "60", "120", "144", "240"];

    public SettingsPanel(Action<System.Numerics.Vector4> applyAccent, Action applyFrameLimit) {
        this.applyAccent = applyAccent;
        this.applyFrameLimit = applyFrameLimit;
    }

    // Label on the left, a modern sliding toggle right-aligned on the row. Returns true on change.
    static bool LabeledToggle(string label, ref bool value, float scale) {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        float switchW = ImGui.GetFrameHeight() * 1.85f;
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - switchW + ImGui.GetCursorPosX() - ImGui.GetStyle().WindowPadding.X);
        return EditorWidgets.ToggleSwitch("##" + label, ref value, scale);
    }

    public void Draw(float scale) {
        if (!Open)
            return;

        ImGui.SetNextWindowSize(new SysVec2(420 * scale, 460 * scale), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Settings", ref Open, ImGuiWindowFlags.NoCollapse)) {
            ImGui.End();
            return;
        }

        EditorPrefs prefs = EditorPrefs.Current;
        var dirty = false;

        if (ImGui.CollapsingHeader("Appearance", ImGuiTreeNodeFlags.DefaultOpen)) {
            var accent = new SysVec3(prefs.AccentR, prefs.AccentG, prefs.AccentB);
            if (ImGui.ColorEdit3("Accent color", ref accent)) {
                prefs.AccentR = accent.X; prefs.AccentG = accent.Y; prefs.AccentB = accent.Z;
                applyAccent(prefs.Accent);
                dirty = true;
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Reset")) {
                prefs.AccentR = 0.239f; prefs.AccentG = 0.545f; prefs.AccentB = 0.831f;
                applyAccent(prefs.Accent);
                dirty = true;
            }

            // UI scale (on top of the auto-detected DPI). RefreshScale() picks the new value up next
            // frame and rebuilds the font/geometry â€” no explicit apply call needed.
            var uiScale = prefs.UiScale;
            if (ImGui.SliderFloat("UI scale", ref uiScale, 0.75f, 2f, "%.2fx")) {
                prefs.UiScale = uiScale;
                dirty = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Editor UI size multiplier (applies on top of monitor DPI).");
        }

        if (ImGui.CollapsingHeader("Viewport", ImGuiTreeNodeFlags.DefaultOpen)) {
            var always = prefs.AlwaysRefresh;
            if (LabeledToggle("Always refresh by default", ref always, scale)) { prefs.AlwaysRefresh = always; dirty = true; }

            var camSpeed = prefs.CameraBaseSpeed;
            if (ImGui.SliderFloat("Camera base speed", ref camSpeed, 1f, 50f)) { prefs.CameraBaseSpeed = camSpeed; dirty = true; }

            var gizmoSize = prefs.GizmoSize;
            if (ImGui.SliderFloat("Gizmo size (px)", ref gizmoSize, 40f, 160f)) { prefs.GizmoSize = gizmoSize; dirty = true; }

            int fpsIndex = Math.Max(0, Array.IndexOf(FrameLimitOptions, prefs.FrameRateLimit));
            if (ImGui.Combo("Frame rate limit", ref fpsIndex, FrameLimitLabels, FrameLimitLabels.Length)) {
                prefs.FrameRateLimit = FrameLimitOptions[fpsIndex];
                applyFrameLimit();
                dirty = true;
            }
        }

        if (ImGui.CollapsingHeader("Grid & Snapping", ImGuiTreeNodeFlags.DefaultOpen)) {
            var showGrid = prefs.ShowGrid;
            if (LabeledToggle("Show grid", ref showGrid, scale)) { prefs.ShowGrid = showGrid; dirty = true; }

            var gridSize = prefs.GridSize;
            if (ImGui.SliderFloat("Grid size", ref gridSize, 0.25f, 10f)) { prefs.GridSize = gridSize; dirty = true; }

            var showGizmos = prefs.ShowGizmos;
            if (LabeledToggle("Show component gizmos", ref showGizmos, scale)) { prefs.ShowGizmos = showGizmos; dirty = true; }

            ImGui.Spacing();
            ImGui.TextDisabled("Hold Ctrl while dragging a gizmo to snap.");
            var sm = prefs.SnapMove;
            if (ImGui.DragFloat("Move snap", ref sm, 0.05f, 0.01f, 100f)) { prefs.SnapMove = sm; dirty = true; }
            var sr = prefs.SnapRotate;
            if (ImGui.DragFloat("Rotate snap (deg)", ref sr, 1f, 1f, 180f)) { prefs.SnapRotate = sr; dirty = true; }
            var ss = prefs.SnapScale;
            if (ImGui.DragFloat("Scale snap", ref ss, 0.05f, 0.01f, 10f)) { prefs.SnapScale = ss; dirty = true; }
        }

        if (dirty)
            EditorPrefs.Save();

        ImGui.End();
    }
}
