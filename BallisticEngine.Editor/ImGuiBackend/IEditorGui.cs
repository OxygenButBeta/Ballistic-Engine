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
    void PopId();
    void BeginDisabled();
    void EndDisabled();
    void SameLine(float offset = 0);
    void Separator();
    void Spacing();
    void Dummy(Vector2 size);
    void SetNextItemWidth(float width);
    float Scale { get; }                       // == EditorTheme.UiScale (replaces the threaded `scale` arg)
    Vector2 ContentRegionAvail { get; }
    Vector2 CursorScreenPos { get; }

    // ---- text ----
    void Text(string text);
    void TextDisabled(string text);
    void TextWrapped(string text);

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
    bool InputText(string label, ref string v, int maxLength);
    bool Combo(string label, ref int index, string[] names);
    bool ColorEdit3(string label, ref Vector3 v, bool hdr);

    // ---- structure ----
    bool TreeNode(string label);
    void TreePop();
    bool Selectable(string label, bool selected);
    bool CollapsingHeader(string label);
    bool BeginChild(string id, Vector2 size, bool border);
    void EndChild();
    bool BeginCombo(string label, string preview);
    void EndCombo();
    bool BeginPopup(string id);
    void EndPopup();
    void OpenPopup(string id);
    bool MenuItem(string label, bool enabled = true);

    // ---- tables (Console / Stats / Build) ----
    bool BeginTable(string id, int columns);
    void EndTable();
    void TableNextRow();
    void TableNextColumn();
    void TableSetupColumn(string label);

    // ---- tooltips / item query ----
    void Tooltip(string text);
    bool IsItemHovered();

    // ---- custom draw + immediate input (curve editor, decorations) ----
    IEditorInput Input { get; }
    IEditorDrawList WindowDrawList { get; }
    uint ColorU32(Vector4 rgba);
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
