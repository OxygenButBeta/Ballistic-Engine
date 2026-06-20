using System.Numerics;
using Hexa.NET.ImGui;

namespace BallisticEngine.Editor;

// The one concrete IEditorGui: forwards every call to Hexa.NET.ImGui. This is a SEAM adapter, so it is
// the allowed place to import ImGui (the enforced boundary). One instance is created per editor session
// and reused for every window each frame — it holds no per-window state (the draw-list/input sub-
// adapters are lightweight structs created on access).
internal sealed class ImGuiEditorGui : IEditorGui {
    readonly ImGuiInputAdapter input = new();
    readonly ImGuiDrawListAdapter drawList = new();

    // ---- layout / id / scope ----
    public void PushId(string id) => ImGui.PushID(id);
    public void PopId() => ImGui.PopID();
    public void BeginDisabled() => ImGui.BeginDisabled();
    public void EndDisabled() => ImGui.EndDisabled();
    public void SameLine(float offset = 0) => ImGui.SameLine(offset);
    public void Separator() => ImGui.Separator();
    public void Spacing() => ImGui.Spacing();
    public void Dummy(Vector2 size) => ImGui.Dummy(size);
    public void SetNextItemWidth(float width) => ImGui.SetNextItemWidth(width);
    public float Scale => EditorTheme.UiScale;
    public Vector2 ContentRegionAvail => ImGui.GetContentRegionAvail();
    public Vector2 CursorScreenPos => ImGui.GetCursorScreenPos();

    // ---- text ----
    public void Text(string text) => ImGui.Text(text);
    public void TextDisabled(string text) => ImGui.TextDisabled(text);
    public void TextWrapped(string text) => ImGui.TextWrapped(text);

    // ---- buttons / toggles ----
    public bool Button(string label, Vector2 size = default) => ImGui.Button(label, size);
    public bool SmallButton(string label) => ImGui.SmallButton(label);
    public bool Checkbox(string label, ref bool v) => ImGui.Checkbox(label, ref v);

    // ---- scalars ----
    public bool SliderFloat(string label, ref float v, float min, float max, string format = "%.3f") =>
        ImGui.SliderFloat(label, ref v, min, max, format);
    public bool SliderInt(string label, ref int v, int min, int max) =>
        ImGui.SliderInt(label, ref v, min, max);
    public bool DragFloat(string label, ref float v, float speed, float min = 0, float max = 0, string format = "%.3f") =>
        ImGui.DragFloat(label, ref v, speed, min, max, format);
    public bool DragFloat2(string label, ref Vector2 v, float speed) => ImGui.DragFloat2(label, ref v, speed);
    public bool DragFloat3(string label, ref Vector3 v, float speed) => ImGui.DragFloat3(label, ref v, speed);
    public bool DragInt(string label, ref int v) => ImGui.DragInt(label, ref v);
    public bool InputText(string label, ref string v, int maxLength) => ImGui.InputText(label, ref v, (uint)maxLength);
    public bool Combo(string label, ref int index, string[] names) => ImGui.Combo(label, ref index, names, names.Length);
    public bool ColorEdit3(string label, ref Vector3 v, bool hdr) =>
        ImGui.ColorEdit3(label, ref v, hdr ? ImGuiColorEditFlags.Hdr | ImGuiColorEditFlags.Float : ImGuiColorEditFlags.None);

    // ---- structure ----
    public bool TreeNode(string label) => ImGui.TreeNode(label);
    public void TreePop() => ImGui.TreePop();
    public bool Selectable(string label, bool selected) => ImGui.Selectable(label, selected);
    public bool CollapsingHeader(string label) => ImGui.CollapsingHeader(label);
    public bool BeginChild(string id, Vector2 size, bool border) =>
        ImGui.BeginChild(id, size, border ? ImGuiChildFlags.Borders : ImGuiChildFlags.None);
    public void EndChild() => ImGui.EndChild();
    public bool BeginCombo(string label, string preview) => ImGui.BeginCombo(label, preview);
    public void EndCombo() => ImGui.EndCombo();
    public bool BeginPopup(string id) => ImGui.BeginPopup(id);
    public void EndPopup() => ImGui.EndPopup();
    public void OpenPopup(string id) => ImGui.OpenPopup(id);
    public bool MenuItem(string label, bool enabled = true) => ImGui.MenuItem(label, "", false, enabled);

    // ---- tables ----
    public bool BeginTable(string id, int columns) => ImGui.BeginTable(id, columns);
    public void EndTable() => ImGui.EndTable();
    public void TableNextRow() => ImGui.TableNextRow();
    public void TableNextColumn() => ImGui.TableNextColumn();
    public void TableSetupColumn(string label) => ImGui.TableSetupColumn(label);

    // ---- tooltips / item query ----
    public void Tooltip(string text) => ImGui.SetTooltip(text);
    public bool IsItemHovered() => ImGui.IsItemHovered();

    // ---- custom draw + input ----
    public IEditorInput Input => input;
    public IEditorDrawList WindowDrawList { get { drawList.Bind(ImGui.GetWindowDrawList()); return drawList; } }
    public uint ColorU32(Vector4 rgba) => ImGui.GetColorU32(rgba);
}

// Mouse/keyboard polling adapter. Stateless — reads the live ImGui IO each call.
internal sealed class ImGuiInputAdapter : IEditorInput {
    public Vector2 MousePos => ImGui.GetMousePos();
    public Vector2 MouseDelta => ImGui.GetIO().MouseDelta;
    public float MouseWheel => ImGui.GetIO().MouseWheel;
    public bool MouseClicked(int button) => ImGui.IsMouseClicked((ImGuiMouseButton)button);
    public bool MouseDoubleClicked(int button) => ImGui.IsMouseDoubleClicked((ImGuiMouseButton)button);
    public bool MouseDown(int button) => ImGui.IsMouseDown((ImGuiMouseButton)button);
    public bool MouseReleased(int button) => ImGui.IsMouseReleased((ImGuiMouseButton)button);
    public bool MouseDragging(int button) => ImGui.IsMouseDragging((ImGuiMouseButton)button);
    public bool KeyPressed(EditorGuiKey key) => ImGui.IsKeyPressed(MapKey(key));
    public bool InvisibleButton(string id, Vector2 size) => ImGui.InvisibleButton(id, size);

    static ImGuiKey MapKey(EditorGuiKey key) => key switch {
        EditorGuiKey.F => ImGuiKey.F,
        EditorGuiKey.Delete => ImGuiKey.Delete,
        EditorGuiKey.Escape => ImGuiKey.Escape,
        EditorGuiKey.Enter => ImGuiKey.Enter,
        EditorGuiKey.LeftArrow => ImGuiKey.LeftArrow,
        EditorGuiKey.RightArrow => ImGuiKey.RightArrow,
        EditorGuiKey.UpArrow => ImGuiKey.UpArrow,
        EditorGuiKey.DownArrow => ImGuiKey.DownArrow,
        _ => ImGuiKey.None,
    };
}

// Draw-list adapter. Rebound each frame to the current window's draw list (the getter on ImGuiEditorGui
// calls Bind before handing it out), so it never holds a stale pointer across windows.
internal sealed class ImGuiDrawListAdapter : IEditorDrawList {
    ImDrawListPtr draw;
    public void Bind(ImDrawListPtr d) => draw = d;

    public void AddLine(Vector2 a, Vector2 b, uint col, float thickness = 1f) => draw.AddLine(a, b, col, thickness);
    public void AddRect(Vector2 min, Vector2 max, uint col, float rounding = 0f, float thickness = 1f) =>
        draw.AddRect(min, max, col, rounding, ImDrawFlags.None, thickness);
    public void AddRectFilled(Vector2 min, Vector2 max, uint col, float rounding = 0f) =>
        draw.AddRectFilled(min, max, col, rounding);
    public void AddCircle(Vector2 center, float radius, uint col, int segments = 0, float thickness = 1f) =>
        draw.AddCircle(center, radius, col, segments, thickness);
    public void AddCircleFilled(Vector2 center, float radius, uint col) => draw.AddCircleFilled(center, radius, col);
    public void AddText(Vector2 pos, uint col, string text) => draw.AddText(pos, col, text);
    public void AddBezierCubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, uint col, float thickness) =>
        draw.AddBezierCubic(p0, p1, p2, p3, col, thickness, 0);
}
