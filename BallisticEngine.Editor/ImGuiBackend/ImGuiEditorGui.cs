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
    public void PushId(int id) => ImGui.PushID(id);
    public void PopId() => ImGui.PopID();
    public void BeginDisabled() => ImGui.BeginDisabled();
    public void BeginDisabled(bool disabled) => ImGui.BeginDisabled(disabled);
    public void EndDisabled() => ImGui.EndDisabled();
    public void SameLine(float offset = 0) => ImGui.SameLine(offset);
    public void SameLine(float offset, float spacing) => ImGui.SameLine(offset, spacing);
    public void Separator() => ImGui.Separator();
    public void Spacing() => ImGui.Spacing();
    public void NewLine() => ImGui.NewLine();
    public void Dummy(Vector2 size) => ImGui.Dummy(size);
    public void AlignTextToFramePadding() => ImGui.AlignTextToFramePadding();
    public void SetNextItemWidth(float width) => ImGui.SetNextItemWidth(width);
    public void Indent(float w = 0) => ImGui.Indent(w);
    public void Unindent(float w = 0) => ImGui.Unindent(w);
    public float Scale => EditorTheme.UiScale;
    public Vector2 ContentRegionAvail => ImGui.GetContentRegionAvail();
    public Vector2 CursorScreenPos => ImGui.GetCursorScreenPos();
    public float CursorPosX { get => ImGui.GetCursorPosX(); set => ImGui.SetCursorPosX(value); }
    public float CursorPosY { get => ImGui.GetCursorPosY(); set => ImGui.SetCursorPosY(value); }
    public float FrameHeight => ImGui.GetFrameHeight();
    public Vector2 WindowPadding => ImGui.GetStyle().WindowPadding;
    public Vector2 ItemSpacing => ImGui.GetStyle().ItemSpacing;
    public Vector2 FramePadding => ImGui.GetStyle().FramePadding;
    public Vector2 CalcTextSize(string text) => ImGui.CalcTextSize(text);

    // ---- scroll ----
    public float ScrollY => ImGui.GetScrollY();
    public float ScrollMaxY => ImGui.GetScrollMaxY();
    public void SetScrollHereY(float ratio = 0.5f) => ImGui.SetScrollHereY(ratio);

    // ---- text ----
    public void Text(string text) => ImGui.Text(text);
    public void TextUnformatted(string text) => ImGui.TextUnformatted(text);
    public void TextDisabled(string text) => ImGui.TextDisabled(text);
    public void TextWrapped(string text) => ImGui.TextWrapped(text);
    public void TextColored(Vector4 color, string text) => ImGui.TextColored(color, text);

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
    public bool InputInt(string label, ref int v, int step = 1) => ImGui.InputInt(label, ref v, step);
    public bool InputText(string label, ref string v, int maxLength) => ImGui.InputText(label, ref v, (uint)maxLength);
    public bool InputTextWithHint(string label, string hint, ref string v, int maxLength) =>
        ImGui.InputTextWithHint(label, hint, ref v, (uint)maxLength);
    public bool Combo(string label, ref int index, string[] names) => ImGui.Combo(label, ref index, names, names.Length);
    public bool ColorEdit3(string label, ref Vector3 v) => ImGui.ColorEdit3(label, ref v);
    public bool ColorEdit3Hdr(string label, ref Vector3 v) =>
        ImGui.ColorEdit3(label, ref v, ImGuiColorEditFlags.Hdr | ImGuiColorEditFlags.Float);
    public void PlotLines(string label, float[] values, int count, string overlay, float min, float max, Vector2 size) {
        if (count <= 0) return;
        ImGui.PlotLines(label, ref values[0], count, 0, overlay, min, max, size);
    }

    // ---- structure ----
    public bool TreeNode(string label) => ImGui.TreeNode(label);
    public void TreePop() => ImGui.TreePop();
    public bool Selectable(string label, bool selected) => ImGui.Selectable(label, selected);
    public bool CollapsingHeader(string label) => ImGui.CollapsingHeader(label);
    public bool CollapsingHeader(string label, bool defaultOpen) =>
        ImGui.CollapsingHeader(label, defaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
    public bool BeginChild(string id, Vector2 size, bool border) =>
        ImGui.BeginChild(id, size, border ? ImGuiChildFlags.Borders : ImGuiChildFlags.None);
    public bool BeginChild(string id, Vector2 size, bool border, bool horizontalScroll) =>
        ImGui.BeginChild(id, size, border ? ImGuiChildFlags.Borders : ImGuiChildFlags.None,
            horizontalScroll ? ImGuiWindowFlags.HorizontalScrollbar : ImGuiWindowFlags.None);
    public void EndChild() => ImGui.EndChild();
    public bool BeginCombo(string label, string preview) => ImGui.BeginCombo(label, preview);
    public void EndCombo() => ImGui.EndCombo();
    public bool BeginPopup(string id) => ImGui.BeginPopup(id);
    public void EndPopup() => ImGui.EndPopup();
    public void OpenPopup(string id) => ImGui.OpenPopup(id);
    public void CloseCurrentPopup() => ImGui.CloseCurrentPopup();
    public void SetNextWindowSizeAppearing(Vector2 size) => ImGui.SetNextWindowSize(size, ImGuiCond.Appearing);
    public bool BeginMenu(string label) => ImGui.BeginMenu(label);
    public void EndMenu() => ImGui.EndMenu();
    public bool MenuItem(string label, bool enabled = true) => ImGui.MenuItem(label, "", false, enabled);
    public bool MenuItem(string label, string shortcut, bool enabled = true) =>
        ImGui.MenuItem(label, shortcut, false, enabled);

    // ---- tables ----
    public bool BeginTable(string id, int columns) => ImGui.BeginTable(id, columns);
    public bool BeginTable(string id, int columns, EditorTableFlags flags, Vector2 outerSize = default) =>
        ImGui.BeginTable(id, columns, MapTableFlags(flags), outerSize);
    public void EndTable() => ImGui.EndTable();
    public void TableNextRow() => ImGui.TableNextRow();
    public void TableNextColumn() => ImGui.TableNextColumn();
    public void TableSetupColumn(string label) => ImGui.TableSetupColumn(label);
    public void TableSetupColumn(string label, EditorColumnFlags flags, float width = 0) =>
        ImGui.TableSetupColumn(label, MapColumnFlags(flags), width);
    public void TableSetupScrollFreeze(int cols, int rows) => ImGui.TableSetupScrollFreeze(cols, rows);
    public void TableHeadersRow() => ImGui.TableHeadersRow();

    static ImGuiTableFlags MapTableFlags(EditorTableFlags f) {
        ImGuiTableFlags r = ImGuiTableFlags.None;
        if (f.HasFlag(EditorTableFlags.RowBg)) r |= ImGuiTableFlags.RowBg;
        if (f.HasFlag(EditorTableFlags.BordersInnerV)) r |= ImGuiTableFlags.BordersInnerV;
        if (f.HasFlag(EditorTableFlags.BordersOuter)) r |= ImGuiTableFlags.BordersOuter;
        if (f.HasFlag(EditorTableFlags.ScrollY)) r |= ImGuiTableFlags.ScrollY;
        if (f.HasFlag(EditorTableFlags.SizingStretchProp)) r |= ImGuiTableFlags.SizingStretchProp;
        if (f.HasFlag(EditorTableFlags.Resizable)) r |= ImGuiTableFlags.Resizable;
        return r;
    }

    static ImGuiTableColumnFlags MapColumnFlags(EditorColumnFlags f) {
        ImGuiTableColumnFlags r = ImGuiTableColumnFlags.None;
        if (f.HasFlag(EditorColumnFlags.WidthStretch)) r |= ImGuiTableColumnFlags.WidthStretch;
        if (f.HasFlag(EditorColumnFlags.WidthFixed)) r |= ImGuiTableColumnFlags.WidthFixed;
        return r;
    }

    // ---- tooltips / item query ----
    public void Tooltip(string text) => ImGui.SetTooltip(text);
    public bool IsItemHovered() => ImGui.IsItemHovered();
    public bool IsItemClicked() => ImGui.IsItemClicked();
    public bool IsItemActive() => ImGui.IsItemActive();
    public bool IsItemActivated() => ImGui.IsItemActivated();
    public bool IsItemDeactivatedAfterEdit() => ImGui.IsItemDeactivatedAfterEdit();

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
