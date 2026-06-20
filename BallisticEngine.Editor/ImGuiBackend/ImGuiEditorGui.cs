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
    public bool ImageButton(string id, nint textureHandle, Vector2 size) =>
        ImGui.ImageButton(id, new ImTextureID((ulong)textureHandle), size, new Vector2(0, 0), new Vector2(1, 1));
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
    public bool InputTextEnter(string label, ref string v, int maxLength) =>
        ImGui.InputText(label, ref v, (uint)maxLength, ImGuiInputTextFlags.EnterReturnsTrue);
    public bool InputTextWithHint(string label, string hint, ref string v, int maxLength) =>
        ImGui.InputTextWithHint(label, hint, ref v, (uint)maxLength);
    public bool Combo(string label, ref int index, string[] names) => ImGui.Combo(label, ref index, names, names.Length);
    public bool ColorEdit3(string label, ref Vector3 v) => ImGui.ColorEdit3(label, ref v);
    public bool ColorEdit3Hdr(string label, ref Vector3 v) =>
        ImGui.ColorEdit3(label, ref v, ImGuiColorEditFlags.Hdr | ImGuiColorEditFlags.Float);
    public bool ColorEdit4(string label, ref Vector4 v) => ImGui.ColorEdit4(label, ref v);
    public void Image(nint textureHandle, Vector2 size) =>
        ImGui.Image(new ImTextureID((ulong)textureHandle), size);
    public void PlotLines(string label, float[] values, int count, string overlay, float min, float max, Vector2 size) {
        if (count <= 0) return;
        ImGui.PlotLines(label, ref values[0], count, 0, overlay, min, max, size);
    }

    // ---- structure ----
    public bool TreeNode(string label) => ImGui.TreeNode(label);
    public bool TreeNodeEx(string label, EditorTreeFlags flags) => ImGui.TreeNodeEx(label, MapTreeFlags(flags));
    public void TreePop() => ImGui.TreePop();
    public void BeginGroup() => ImGui.BeginGroup();
    public void EndGroup() => ImGui.EndGroup();
    public float TreeNodeToLabelSpacing => ImGui.GetTreeNodeToLabelSpacing();
    public bool Selectable(string label, bool selected = false) => ImGui.Selectable(label, selected);
    public bool Selectable(string label, bool selected, Vector2 size) =>
        ImGui.Selectable(label, selected, ImGuiSelectableFlags.None, size);
    public bool SelectableRow(string label, bool selected) =>
        ImGui.Selectable(label, selected, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick);
    public bool CollapsingHeader(string label) => ImGui.CollapsingHeader(label);
    public bool CollapsingHeader(string label, bool defaultOpen) =>
        ImGui.CollapsingHeader(label, defaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
    public bool CollapsingHeaderFramed(string label) =>
        ImGui.CollapsingHeader(label, ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.Framed);
    public bool CollapsingHeaderFramedOverlay(string label) =>
        ImGui.CollapsingHeader(label, ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.AllowOverlap | ImGuiTreeNodeFlags.Framed);

    static ImGuiTreeNodeFlags MapTreeFlags(EditorTreeFlags f) {
        ImGuiTreeNodeFlags r = ImGuiTreeNodeFlags.None;
        if (f.HasFlag(EditorTreeFlags.DefaultOpen)) r |= ImGuiTreeNodeFlags.DefaultOpen;
        if (f.HasFlag(EditorTreeFlags.Framed)) r |= ImGuiTreeNodeFlags.Framed;
        if (f.HasFlag(EditorTreeFlags.SpanAvailWidth)) r |= ImGuiTreeNodeFlags.SpanAvailWidth;
        if (f.HasFlag(EditorTreeFlags.OpenOnArrow)) r |= ImGuiTreeNodeFlags.OpenOnArrow;
        if (f.HasFlag(EditorTreeFlags.AllowOverlap)) r |= ImGuiTreeNodeFlags.AllowOverlap;
        if (f.HasFlag(EditorTreeFlags.Selected)) r |= ImGuiTreeNodeFlags.Selected;
        if (f.HasFlag(EditorTreeFlags.Leaf)) r |= ImGuiTreeNodeFlags.Leaf;
        if (f.HasFlag(EditorTreeFlags.NoTreePushOnOpen)) r |= ImGuiTreeNodeFlags.NoTreePushOnOpen;
        return r;
    }
    public bool BeginChild(string id, Vector2 size, bool border) =>
        ImGui.BeginChild(id, size, border ? ImGuiChildFlags.Borders : ImGuiChildFlags.None);
    public bool BeginChild(string id, Vector2 size, bool border, bool horizontalScroll) =>
        ImGui.BeginChild(id, size, border ? ImGuiChildFlags.Borders : ImGuiChildFlags.None,
            horizontalScroll ? ImGuiWindowFlags.HorizontalScrollbar : ImGuiWindowFlags.None);
    public bool BeginChildAutoResizeY(string id, bool border) =>
        ImGui.BeginChild(id, default,
            (border ? ImGuiChildFlags.Borders : ImGuiChildFlags.None) | ImGuiChildFlags.AutoResizeY);
    public void EndChild() => ImGui.EndChild();
    public bool BeginCombo(string label, string preview) => ImGui.BeginCombo(label, preview);
    public void EndCombo() => ImGui.EndCombo();
    public bool BeginPopup(string id) => ImGui.BeginPopup(id);
    public bool BeginPopupModalAutoResize(string id) => ImGui.BeginPopupModal(id, ImGuiWindowFlags.AlwaysAutoResize);
    public void EndPopup() => ImGui.EndPopup();
    public void OpenPopup(string id) => ImGui.OpenPopup(id);
    public bool BeginPopupContextItem(string id) => ImGui.BeginPopupContextItem(id);
    public void OpenPopupOnItemClick(string id) => ImGui.OpenPopupOnItemClick(id, ImGuiPopupFlags.MouseButtonRight);
    public void CloseCurrentPopup() => ImGui.CloseCurrentPopup();
    public void SetNextWindowSizeAppearing(Vector2 size) => ImGui.SetNextWindowSize(size, ImGuiCond.Appearing);
    public bool BeginMenu(string label) => ImGui.BeginMenu(label);
    public void EndMenu() => ImGui.EndMenu();
    public bool MenuItem(string label, bool enabled = true) => ImGui.MenuItem(label, "", false, enabled);
    public bool MenuItem(string label, string shortcut, bool enabled = true) =>
        ImGui.MenuItem(label, shortcut, false, enabled);
    public bool MenuItem(string label, string shortcut, bool selected, bool enabled) =>
        ImGui.MenuItem(label, shortcut, selected, enabled);
    public bool MenuItemToggle(string label, ref bool selected) => ImGui.MenuItem(label, "", ref selected);
    public bool FramedHeader(string label) => ImGui.TreeNodeEx(label,
        ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.AllowOverlap | ImGuiTreeNodeFlags.Framed |
        ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.NoTreePushOnOpen);

    // ---- tables ----
    public bool BeginTable(string id, int columns) => ImGui.BeginTable(id, columns);
    public bool BeginTable(string id, int columns, EditorTableFlags flags, Vector2 outerSize = default) =>
        ImGui.BeginTable(id, columns, MapTableFlags(flags), outerSize);
    public void EndTable() => ImGui.EndTable();
    public void TableNextRow() => ImGui.TableNextRow();
    public void TableNextColumn() => ImGui.TableNextColumn();
    public void TableSetColumnIndex(int column) => ImGui.TableSetColumnIndex(column);
    public void TableSetupColumn(string label) => ImGui.TableSetupColumn(label);
    public void TableSetupColumn(string label, EditorColumnFlags flags, float width = 0) =>
        ImGui.TableSetupColumn(label, MapColumnFlags(flags), width);
    public void TableSetupScrollFreeze(int cols, int rows) => ImGui.TableSetupScrollFreeze(cols, rows);
    public void TableHeadersRow() => ImGui.TableHeadersRow();
    public void TableSetRowBgColor(uint color) => ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, color);
    public unsafe bool TableGetSortSpec(out int column, out bool ascending) {
        column = 0; ascending = true;
        ImGuiTableSortSpecsPtr specs = ImGui.TableGetSortSpecs();
        if (specs.IsNull || specs.SpecsCount == 0) return false;
        column = specs.Specs.ColumnIndex;
        ascending = specs.Specs.SortDirection == ImGuiSortDirection.Ascending;
        return true;
    }

    static ImGuiTableFlags MapTableFlags(EditorTableFlags f) {
        ImGuiTableFlags r = ImGuiTableFlags.None;
        if (f.HasFlag(EditorTableFlags.RowBg)) r |= ImGuiTableFlags.RowBg;
        if (f.HasFlag(EditorTableFlags.BordersInnerV)) r |= ImGuiTableFlags.BordersInnerV;
        if (f.HasFlag(EditorTableFlags.BordersOuter)) r |= ImGuiTableFlags.BordersOuter;
        if (f.HasFlag(EditorTableFlags.ScrollY)) r |= ImGuiTableFlags.ScrollY;
        if (f.HasFlag(EditorTableFlags.SizingStretchProp)) r |= ImGuiTableFlags.SizingStretchProp;
        if (f.HasFlag(EditorTableFlags.Resizable)) r |= ImGuiTableFlags.Resizable;
        if (f.HasFlag(EditorTableFlags.PadOuterX)) r |= ImGuiTableFlags.PadOuterX;
        if (f.HasFlag(EditorTableFlags.SizingFixedFit)) r |= ImGuiTableFlags.SizingFixedFit;
        if (f.HasFlag(EditorTableFlags.Sortable)) r |= ImGuiTableFlags.Sortable;
        return r;
    }

    static ImGuiTableColumnFlags MapColumnFlags(EditorColumnFlags f) {
        ImGuiTableColumnFlags r = ImGuiTableColumnFlags.None;
        if (f.HasFlag(EditorColumnFlags.WidthStretch)) r |= ImGuiTableColumnFlags.WidthStretch;
        if (f.HasFlag(EditorColumnFlags.WidthFixed)) r |= ImGuiTableColumnFlags.WidthFixed;
        if (f.HasFlag(EditorColumnFlags.DefaultSort)) r |= ImGuiTableColumnFlags.DefaultSort;
        return r;
    }

    // ---- tooltips / item query ----
    public void Tooltip(string text) => ImGui.SetTooltip(text);
    public bool IsItemHovered() => ImGui.IsItemHovered();
    public bool IsWindowHovered() => ImGui.IsWindowHovered();
    public bool IsWindowHoveredAllowBlocked() => ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
    public bool IsAnyItemHovered() => ImGui.IsAnyItemHovered();
    public bool IsMouseClicked(int button) => ImGui.IsMouseClicked((ImGuiMouseButton)button);
    public void SetMouseCursorResizeEW() => ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
    public void CenterNextWindow(Vector2 size) {
        ImGui.SetNextWindowSize(size, ImGuiCond.Appearing);
        CenterNextWindowPos();
    }
    public void CenterNextWindowPos() {
        ImGuiViewportPtr vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(new Vector2(vp.Pos.X + vp.Size.X * 0.5f, vp.Pos.Y + vp.Size.Y * 0.5f),
            ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
    }
    public bool IsMouseHoveringRect(Vector2 min, Vector2 max, bool clip = true) => ImGui.IsMouseHoveringRect(min, max, clip);
    public bool IsItemClicked() => ImGui.IsItemClicked();
    public bool IsItemActive() => ImGui.IsItemActive();
    public bool IsItemActivated() => ImGui.IsItemActivated();
    public bool IsItemFocused() => ImGui.IsItemFocused();
    public bool IsItemDeactivatedAfterEdit() => ImGui.IsItemDeactivatedAfterEdit();
    public bool IsAnyItemActive() => ImGui.IsAnyItemActive();
    public float TextLineHeightWithSpacing => ImGui.GetTextLineHeightWithSpacing();
    public float FrameHeightWithSpacing => ImGui.GetFrameHeightWithSpacing();

    // ---- item geometry + focus ----
    public Vector2 ItemRectMin => ImGui.GetItemRectMin();
    public Vector2 ItemRectMax => ImGui.GetItemRectMax();
    public Vector2 WindowPos => ImGui.GetWindowPos();
    public Vector2 WindowSize => ImGui.GetWindowSize();
    public void SetCursorScreenPos(Vector2 pos) => ImGui.SetCursorScreenPos(pos);
    public bool IsWindowAppearing() => ImGui.IsWindowAppearing();
    public void SetKeyboardFocusHere() => ImGui.SetKeyboardFocusHere();
    public bool KeyPressed(EditorGuiKey key) => input.KeyPressed(key);

    // ---- drag-drop (sources) ----
    public bool BeginDragDropSource() => ImGui.BeginDragDropSource();
    public void EndDragDropSource() => ImGui.EndDragDropSource();
    public unsafe void SetDragDropPayloadInt(string type, int value) =>
        ImGui.SetDragDropPayload(type, &value, sizeof(int));
    public unsafe void SetDragDropPayloadString(string type, string value) {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(value ?? "");
        fixed (byte* p = bytes)
            ImGui.SetDragDropPayload(type, p, (nuint)bytes.Length);
    }
    public unsafe void SetDragDropPayloadBytes(string type, byte[] payload) {
        fixed (byte* p = payload)
            ImGui.SetDragDropPayload(type, p, (nuint)payload.Length);
    }

    // ---- misc item / window / tree / input ----
    public bool IsItemDeactivated() => ImGui.IsItemDeactivated();
    public bool IsItemToggledOpen() => ImGui.IsItemToggledOpen();
    public bool IsWindowFocused() => ImGui.IsWindowFocused();
    public bool IsWindowFocusedIncludingChildren() => ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
    public bool IsMouseDoubleClicked(int button) => ImGui.IsMouseDoubleClicked((ImGuiMouseButton)button);
    public void SetNextItemOpen(bool open) => ImGui.SetNextItemOpen(open);
    public void SetNextItemOpenOnce(bool open) => ImGui.SetNextItemOpen(open, ImGuiCond.Once);
    public void SetNextItemOpenAlways(bool open) => ImGui.SetNextItemOpen(open, ImGuiCond.Always);
    public bool BeginPopupContextWindow(string id) => ImGui.BeginPopupContextWindow(id);
    public bool BeginPopupContextWindowEmpty(string id) =>
        ImGui.BeginPopupContextWindow(id, ImGuiPopupFlags.MouseButtonRight | ImGuiPopupFlags.NoOpenOverItems);
    public bool WantTextInput => ImGui.GetIO().WantTextInput;
    public bool KeyCtrl => ImGui.GetIO().KeyCtrl;
    public bool KeyShift => ImGui.GetIO().KeyShift;

    // ---- drag-drop (targets) ----
    public bool BeginDragDropTarget() => ImGui.BeginDragDropTarget();
    public void EndDragDropTarget() => ImGui.EndDragDropTarget();

    public unsafe int? AcceptDragDropPayloadInt(string type) {
        ImGuiPayloadPtr payload = ImGui.AcceptDragDropPayload(type);
        if (payload.IsNull || payload.Data == null) return null;
        return *(int*)payload.Data;
    }

    public unsafe string AcceptDragDropPayloadString(string type) {
        ImGuiPayloadPtr payload = ImGui.AcceptDragDropPayload(type);
        if (payload.IsNull || payload.Data == null) return null;
        // The asset drag payload is a byte string of payload.DataSize bytes (the asset browser writes it as
        // ANSI; match PtrToStringAnsi exactly so the GUID-list parsing downstream is byte-identical).
        return System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)payload.Data, payload.DataSize);
    }

    // ---- fonts ----
    public void PushFont(EditorFont font) => ImGui.PushFont(MapFont(font));
    public void PopFont() => ImGui.PopFont();
    public float FontSize => ImGui.GetFontSize();
    public float FontSizeOf(EditorFont font) => MapFont(font).FontSize;
    public float TextLineHeight => ImGui.GetTextLineHeight();

    static ImFontPtr MapFont(EditorFont f) => f switch {
        EditorFont.Header => EditorTheme.Header,
        EditorFont.Caption => EditorTheme.Caption,
        EditorFont.Display => EditorTheme.Display,
        EditorFont.Bold => ImGuiController.Bold,
        EditorFont.LargeIcons => ImGuiController.LargeIcons,
        _ => EditorTheme.Body,
    };

    // ---- style scope ----
    public void PushColor(EditorStyleColor which, Vector4 rgba) => ImGui.PushStyleColor(MapColor(which), rgba);
    public void PopColor(int count = 1) => ImGui.PopStyleColor(count);
    public void PushFramePadding(Vector2 padding) => ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, padding);
    public void PushItemSpacing(Vector2 spacing) => ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, spacing);
    public void PushWindowPadding(Vector2 padding) => ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, padding);
    public void PushFrameRounding(float rounding) => ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, rounding);
    public void PushAlphaScaled(float factor) => ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * factor);
    public void PushPopupBg(Vector4 rgba) => ImGui.PushStyleColor(ImGuiCol.PopupBg, rgba);
    public void PopStyleVar(int count = 1) => ImGui.PopStyleVar(count);
    public float FrameRounding => ImGui.GetStyle().FrameRounding;
    public float IndentSpacing => ImGui.GetStyle().IndentSpacing;
    public float Alpha => ImGui.GetStyle().Alpha;
    public Vector4 StyleColor(EditorStyleColor which) => ImGui.GetStyle().Colors[(int)MapColor(which)];

    static ImGuiCol MapColor(EditorStyleColor c) => c switch {
        EditorStyleColor.Text => ImGuiCol.Text,
        EditorStyleColor.TextDisabled => ImGuiCol.TextDisabled,
        EditorStyleColor.Button => ImGuiCol.Button,
        EditorStyleColor.ButtonHovered => ImGuiCol.ButtonHovered,
        EditorStyleColor.ButtonActive => ImGuiCol.ButtonActive,
        EditorStyleColor.FrameBg => ImGuiCol.FrameBg,
        EditorStyleColor.FrameBgHovered => ImGuiCol.FrameBgHovered,
        EditorStyleColor.SliderGrab => ImGuiCol.SliderGrab,
        EditorStyleColor.CheckMark => ImGuiCol.CheckMark,
        EditorStyleColor.ChildBg => ImGuiCol.ChildBg,
        EditorStyleColor.Border => ImGuiCol.Border,
        _ => ImGuiCol.Text,
    };

    // ---- misc window metrics / clipboard ----
    public float WindowWidth => ImGui.GetWindowWidth();
    public float ScrollbarSize => ImGui.GetStyle().ScrollbarSize;
    public void SetClipboardText(string text) => ImGui.SetClipboardText(text);

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
        EditorGuiKey.A => ImGuiKey.A,
        EditorGuiKey.D => ImGuiKey.D,
        EditorGuiKey.G => ImGuiKey.G,
        EditorGuiKey.F2 => ImGuiKey.F2,
        EditorGuiKey.C => ImGuiKey.C,
        EditorGuiKey.X => ImGuiKey.X,
        EditorGuiKey.V => ImGuiKey.V,
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
    public void AddRectFilled(Vector2 min, Vector2 max, uint col, float rounding, EditorCorner corners) =>
        draw.AddRectFilled(min, max, col, rounding, MapCorner(corners));

    static ImDrawFlags MapCorner(EditorCorner c) {
        ImDrawFlags r = ImDrawFlags.None;
        if (c.HasFlag(EditorCorner.TopLeft)) r |= ImDrawFlags.RoundCornersTopLeft;
        if (c.HasFlag(EditorCorner.TopRight)) r |= ImDrawFlags.RoundCornersTopRight;
        if (c.HasFlag(EditorCorner.BottomLeft)) r |= ImDrawFlags.RoundCornersBottomLeft;
        if (c.HasFlag(EditorCorner.BottomRight)) r |= ImDrawFlags.RoundCornersBottomRight;
        return r;
    }
    public void AddCircle(Vector2 center, float radius, uint col, int segments = 0, float thickness = 1f) =>
        draw.AddCircle(center, radius, col, segments, thickness);
    public void AddCircleFilled(Vector2 center, float radius, uint col) => draw.AddCircleFilled(center, radius, col);
    public void AddText(Vector2 pos, uint col, string text) => draw.AddText(pos, col, text);
    public unsafe void AddText(EditorFont font, float size, Vector2 pos, uint col, string text) =>
        draw.AddText(MapFont(font), size, pos, col, text);

    static ImFontPtr MapFont(EditorFont f) => f switch {
        EditorFont.Header => EditorTheme.Header,
        EditorFont.Caption => EditorTheme.Caption,
        EditorFont.Display => EditorTheme.Display,
        EditorFont.Bold => ImGuiController.Bold,
        EditorFont.LargeIcons => ImGuiController.LargeIcons,
        _ => EditorTheme.Body,
    };

    public void ChannelsSplit(int count) => draw.ChannelsSplit(count);
    public void ChannelsSetCurrent(int channel) => draw.ChannelsSetCurrent(channel);
    public void ChannelsMerge() => draw.ChannelsMerge();
    public void PushClipRect(Vector2 min, Vector2 max, bool intersectWithCurrent) =>
        draw.PushClipRect(min, max, intersectWithCurrent);
    public void PopClipRect() => draw.PopClipRect();
    public void AddBezierCubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, uint col, float thickness) =>
        draw.AddBezierCubic(p0, p1, p2, p3, col, thickness, 0);
}
