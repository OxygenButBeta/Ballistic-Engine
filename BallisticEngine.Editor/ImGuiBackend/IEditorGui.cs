using System.Numerics;

namespace BallisticEngine.Editor;

// The universal immediate-mode GUI seam for editor windows (the Unity-style EditorWindow framework).
// Every EditorWindow body talks ONLY to this — never to Hexa.NET.ImGui directly — so window/panel code
// carries no ImGui import and the backend can be swapped or faked. One concrete implementation
// (ImGuiEditorGui) forwards to Hexa.NET.ImGui; tests can implement a recording fake.
//
// This is DELIBERATELY separate from the inspector's IInspectorGui (the row-oriented sub-seam:
// BeginRow/override-checkbox/mixed-value marker). IInspectorGui layers ON TOP of this for the member
// pipeline; this one is the general windowing + widget surface every panel uses.
//
// Scope notes (the pragmatic "zero raw ImGui" boundary): printf-style format strings ("%.3f") pass
// through unchanged (an accepted ImGui-convention leak); style push/pop (grab tints, frame padding)
// is NOT modelled here — the two inspector adapters keep those few raw-ImGui sites. Window Begin/End is
// owned by WindowShell, not exposed here, so a window body cannot mismatch a Begin with an End.
public interface IEditorGui {
    // ---- layout / id / scope ----
    void PushId(string id);
    void PushId(int id);
    void PopId();
    void BeginDisabled();
    void BeginDisabled(bool disabled);
    void EndDisabled();
    void SameLine(float offset = 0);
    void SameLine(float offset, float spacing);
    void Separator();
    void Spacing();
    void NewLine();
    void Dummy(Vector2 size);
    void AlignTextToFramePadding();
    void SetNextItemWidth(float width);
    void Indent(float w = 0);
    void Unindent(float w = 0);
    float Scale { get; }                       // == EditorTheme.UiScale (replaces the threaded `scale` arg)
    Vector2 ContentRegionAvail { get; }
    Vector2 CursorScreenPos { get; }
    float CursorPosX { get; set; }
    float CursorPosY { get; set; }
    float FrameHeight { get; }
    Vector2 WindowPadding { get; }             // ImGui.GetStyle().WindowPadding (for right-aligned widgets)
    Vector2 ItemSpacing { get; }               // ImGui.GetStyle().ItemSpacing
    Vector2 FramePadding { get; }              // ImGui.GetStyle().FramePadding
    Vector2 CalcTextSize(string text);

    // ---- scroll (log-tailing panels) ----
    float ScrollY { get; }
    float ScrollMaxY { get; }
    void SetScrollHereY(float ratio = 0.5f);

    // ---- text ----
    void Text(string text);
    void TextUnformatted(string text);         // no printf parsing (literal text with '%' etc.)
    void TextDisabled(string text);
    void TextWrapped(string text);
    void TextColored(Vector4 color, string text);

    // ---- buttons / toggles ----
    bool Button(string label, Vector2 size = default);
    bool SmallButton(string label);
    bool Checkbox(string label, ref bool v);

    // ---- scalars (cover both inspector value widgets and free window use) ----
    bool SliderFloat(string label, ref float v, float min, float max, string format = "%.3f");
    bool SliderInt(string label, ref int v, int min, int max);
    bool DragFloat(string label, ref float v, float speed, float min = 0, float max = 0, string format = "%.3f");
    bool DragFloat2(string label, ref Vector2 v, float speed);
    bool DragFloat3(string label, ref Vector3 v, float speed);
    bool DragInt(string label, ref int v);
    bool InputInt(string label, ref int v, int step = 1);
    bool InputText(string label, ref string v, int maxLength);
    bool InputTextWithHint(string label, string hint, ref string v, int maxLength);
    bool Combo(string label, ref int index, string[] names);
    bool ColorEdit3(string label, ref Vector3 v);
    bool ColorEdit3Hdr(string label, ref Vector3 v);
    void PlotLines(string label, float[] values, int count, string overlay, float min, float max, Vector2 size);

    // ---- structure ----
    bool TreeNode(string label);
    void TreePop();
    bool Selectable(string label, bool selected);
    bool CollapsingHeader(string label);
    bool CollapsingHeader(string label, bool defaultOpen);
    bool BeginChild(string id, Vector2 size, bool border);
    bool BeginChild(string id, Vector2 size, bool border, bool horizontalScroll);
    void EndChild();
    bool BeginCombo(string label, string preview);
    void EndCombo();
    bool BeginPopup(string id);
    void EndPopup();
    void OpenPopup(string id);
    void CloseCurrentPopup();
    void SetNextWindowSizeAppearing(Vector2 size);   // popups that want a sensible first-open size
    bool BeginMenu(string label);
    void EndMenu();
    bool MenuItem(string label, bool enabled = true);
    bool MenuItem(string label, string shortcut, bool enabled = true);

    // ---- tables (Console / Stats / Build / Profiler) ----
    bool BeginTable(string id, int columns);
    bool BeginTable(string id, int columns, EditorTableFlags flags, Vector2 outerSize = default);
    void EndTable();
    void TableNextRow();
    void TableNextColumn();
    void TableSetColumnIndex(int column);
    void TableSetupColumn(string label);
    void TableSetupColumn(string label, EditorColumnFlags flags, float width = 0);
    void TableSetupScrollFreeze(int cols, int rows);
    void TableHeadersRow();

    // ---- tooltips / item query ----
    void Tooltip(string text);
    bool IsItemHovered();
    bool IsItemClicked();
    bool IsItemActive();
    bool IsItemActivated();
    bool IsItemDeactivatedAfterEdit();

    // ---- style scope (push/pop, balanced) ----
    // Lets a window body tint a few widgets (severity colours, filter chips, tighter checkbox padding)
    // WITHOUT importing ImGui's style enums. Push N colours then Pop the SAME N; push one var then PopVar.
    // Covers exactly the cases the panels use today (Text + Button family + FramePadding); extend on demand.
    void PushColor(EditorStyleColor which, Vector4 rgba);
    void PopColor(int count = 1);
    void PushFramePadding(Vector2 padding);
    void PopStyleVar(int count = 1);

    // ---- misc window metrics / clipboard ----
    float WindowWidth { get; }
    float ScrollbarSize { get; }
    void SetClipboardText(string text);

    // ---- custom draw + immediate input (curve editor, decorations) ----
    IEditorInput Input { get; }
    IEditorDrawList WindowDrawList { get; }
    uint ColorU32(Vector4 rgba);
    Vector4 StyleColor(EditorStyleColor which);   // read the current themed colour (e.g. TextDisabled)
}

// Seam-local style-colour ids (subset the panels tint) — keeps ImGuiCol out of window bodies.
public enum EditorStyleColor {
    Text, TextDisabled,
    Button, ButtonHovered, ButtonActive,
    FrameBg, FrameBgHovered,
    SliderGrab,
}

// Seam-local table flags (subset actually used by panels) — keeps ImGuiTableFlags out of window bodies.
[System.Flags]
public enum EditorTableFlags {
    None = 0,
    RowBg = 1 << 0,
    BordersInnerV = 1 << 1,
    BordersOuter = 1 << 2,
    ScrollY = 1 << 3,
    SizingStretchProp = 1 << 4,
    Resizable = 1 << 5,
}

// Seam-local column flags (subset).
[System.Flags]
public enum EditorColumnFlags {
    None = 0,
    WidthStretch = 1 << 0,
    WidthFixed = 1 << 1,
}

// Immediate mouse/keyboard polling for custom-drawn surfaces (the curve editor's key drag, pan/zoom,
// hotkeys). Mirrors exactly the ImGui IO / hit-test calls audited in CurveEditorWindow.
public interface IEditorInput {
    Vector2 MousePos { get; }
    Vector2 MouseDelta { get; }
    float MouseWheel { get; }
    bool MouseClicked(int button);
    bool MouseDoubleClicked(int button);
    bool MouseDown(int button);
    bool MouseReleased(int button);
    bool MouseDragging(int button);
    bool KeyPressed(EditorGuiKey key);
    bool InvisibleButton(string id, Vector2 size);
}

// The draw-list surface for windows that paint their own geometry (curve editor, component banding,
// axis chips, decorations). Covers exactly the Add* calls those sites use today; more can be added on
// demand (YAGNI — clip rects / channel split / convex polys are not needed yet).
public interface IEditorDrawList {
    void AddLine(Vector2 a, Vector2 b, uint col, float thickness = 1f);
    void AddRect(Vector2 min, Vector2 max, uint col, float rounding = 0f, float thickness = 1f);
    void AddRectFilled(Vector2 min, Vector2 max, uint col, float rounding = 0f);
    void AddCircle(Vector2 center, float radius, uint col, int segments = 0, float thickness = 1f);
    void AddCircleFilled(Vector2 center, float radius, uint col);
    void AddText(Vector2 pos, uint col, string text);
    void AddBezierCubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, uint col, float thickness);
}

// GUI-side key enum (the seam never exposes ImGuiKey). The adapter maps these to ImGuiKey. Named with a
// Gui suffix to avoid the editor-CAMERA's EditorKey (W/A/S/D...). Extend as windows need more keys;
// today only the curve editor's hotkeys are required.
public enum EditorGuiKey {
    F, Delete, Escape, Enter,
    LeftArrow, RightArrow, UpArrow, DownArrow,
}
