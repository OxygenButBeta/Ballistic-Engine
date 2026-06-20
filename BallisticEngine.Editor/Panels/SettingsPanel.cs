using System.Numerics;

namespace BallisticEngine.Editor;

// Project/editor Settings window (toggled from the Window menu). Edits EditorPrefs.Current live —
// every change persists immediately and accent changes re-apply the theme through the supplied
// callback. Open is owned by the base (the Window menu toggles it).
//
// Phase-2 EditorWindow: the body draws through IEditorGui (no raw ImGui). WindowShell owns Begin/End.
// The custom ToggleSwitch widget stays an EditorWidgets helper call (a seam-adjacent draw-list widget).
internal sealed class SettingsPanel : EditorWindow {
    readonly Action<System.Numerics.Vector4> applyAccent;
    readonly Action applyFrameLimit;

    // Shared with the toolbar's FPS popup so both edit the same presets.
    public static readonly int[] FrameLimitOptions = [0, 30, 60, 120, 144, 240];
    public static readonly string[] FrameLimitLabels = ["VSync", "30", "60", "120", "144", "240"];

    public SettingsPanel(Action<System.Numerics.Vector4> applyAccent, Action applyFrameLimit) {
        this.applyAccent = applyAccent;
        this.applyFrameLimit = applyFrameLimit;
        DockKey = "win.settings";
        Title = "Settings";
        Icon = null;             // Settings had no inline icon in its title
        NoCollapse = true;
        DesiredSize = new Vector2(420, 460);
    }

    // Label on the left, a modern sliding toggle right-aligned on the row. Returns true on change.
    static bool LabeledToggle(IEditorGui gui, string label, ref bool value) {
        gui.AlignTextToFramePadding();
        gui.TextUnformatted(label);
        float switchW = gui.FrameHeight * 1.85f;
        gui.SameLine(gui.ContentRegionAvail.X - switchW + gui.CursorPosX - gui.WindowPadding.X);
        return EditorWidgets.ToggleSwitch("##" + label, ref value, gui.Scale);
    }

    protected override void OnGui(IEditorGui gui) {
        EditorPrefs prefs = EditorPrefs.Current;
        var dirty = false;

        if (gui.CollapsingHeader("Appearance", defaultOpen: true)) {
            var accent = new Vector3(prefs.AccentR, prefs.AccentG, prefs.AccentB);
            if (gui.ColorEdit3("Accent color", ref accent)) {
                prefs.AccentR = accent.X; prefs.AccentG = accent.Y; prefs.AccentB = accent.Z;
                applyAccent(prefs.Accent);
                dirty = true;
            }
            gui.SameLine();
            if (gui.SmallButton("Reset")) {
                prefs.AccentR = 0.239f; prefs.AccentG = 0.545f; prefs.AccentB = 0.831f;
                applyAccent(prefs.Accent);
                dirty = true;
            }

            // UI scale (on top of the auto-detected DPI). RefreshScale() picks the new value up next
            // frame and rebuilds the font/geometry — no explicit apply call needed.
            var uiScale = prefs.UiScale;
            if (gui.SliderFloat("UI scale", ref uiScale, 0.75f, 2f, "%.2fx")) {
                prefs.UiScale = uiScale;
                dirty = true;
            }
            if (gui.IsItemHovered())
                gui.Tooltip("Editor UI size multiplier (applies on top of monitor DPI).");
        }

        if (gui.CollapsingHeader("Viewport", defaultOpen: true)) {
            var always = prefs.AlwaysRefresh;
            if (LabeledToggle(gui, "Always refresh by default", ref always)) { prefs.AlwaysRefresh = always; dirty = true; }

            var camSpeed = prefs.CameraBaseSpeed;
            if (gui.SliderFloat("Camera base speed", ref camSpeed, 1f, 50f)) { prefs.CameraBaseSpeed = camSpeed; dirty = true; }

            var gizmoSize = prefs.GizmoSize;
            if (gui.SliderFloat("Gizmo size (px)", ref gizmoSize, 40f, 160f)) { prefs.GizmoSize = gizmoSize; dirty = true; }

            int fpsIndex = Math.Max(0, Array.IndexOf(FrameLimitOptions, prefs.FrameRateLimit));
            if (gui.Combo("Frame rate limit", ref fpsIndex, FrameLimitLabels)) {
                prefs.FrameRateLimit = FrameLimitOptions[fpsIndex];
                applyFrameLimit();
                dirty = true;
            }
        }

        if (gui.CollapsingHeader("Grid & Snapping", defaultOpen: true)) {
            var showGrid = prefs.ShowGrid;
            if (LabeledToggle(gui, "Show grid", ref showGrid)) { prefs.ShowGrid = showGrid; dirty = true; }

            var gridSize = prefs.GridSize;
            if (gui.SliderFloat("Grid size", ref gridSize, 0.25f, 10f)) { prefs.GridSize = gridSize; dirty = true; }

            var showGizmos = prefs.ShowGizmos;
            if (LabeledToggle(gui, "Show component gizmos", ref showGizmos)) { prefs.ShowGizmos = showGizmos; dirty = true; }

            gui.Spacing();
            gui.TextDisabled("Hold Ctrl while dragging a gizmo to snap.");
            var sm = prefs.SnapMove;
            if (gui.DragFloat("Move snap", ref sm, 0.05f, 0.01f, 100f)) { prefs.SnapMove = sm; dirty = true; }
            var sr = prefs.SnapRotate;
            if (gui.DragFloat("Rotate snap (deg)", ref sr, 1f, 1f, 180f)) { prefs.SnapRotate = sr; dirty = true; }
            var ss = prefs.SnapScale;
            if (gui.DragFloat("Scale snap", ref ss, 0.05f, 0.01f, 10f)) { prefs.SnapScale = ss; dirty = true; }
        }

        if (dirty)
            EditorPrefs.Save();
    }
}
