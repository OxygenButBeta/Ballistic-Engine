namespace BallisticEngine.Editor;

public interface IEditorGui {
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
    float Scale { get; }
    Vector2 ContentRegionAvail { get; }
    Vector2 CursorScreenPos { get; }
    float CursorPosX { get; set; }
    float CursorPosY { get; set; }
    float FrameHeight { get; }
    Vector2 WindowPadding { get; }
    Vector2 ItemSpacing { get; }
    Vector2 FramePadding { get; }
    Vector2 CalcTextSize(string text);

    float ScrollY { get; }
    float ScrollMaxY { get; }
    void SetScrollHereY(float ratio = 0.5f);

    void Text(string text);
    void TextUnformatted(string text);
    void TextDisabled(string text);
    void TextWrapped(string text);
    void TextColored(Vector4 color, string text);

    bool Button(string label, Vector2 size = default);
    bool SmallButton(string label);
    bool ImageButton(string id, nint textureHandle, Vector2 size);
    bool Checkbox(string label, ref bool v);

    bool SliderFloat(string label, ref float v, float min, float max, string format = "%.3f");
    bool SliderInt(string label, ref int v, int min, int max);
    bool DragFloat(string label, ref float v, float speed, float min = 0, float max = 0, string format = "%.3f");
    bool DragFloat2(string label, ref Vector2 v, float speed);
    bool DragFloat3(string label, ref Vector3 v, float speed);
    bool DragInt(string label, ref int v);
    bool InputInt(string label, ref int v, int step = 1);
    bool InputText(string label, ref string v, int maxLength);
    bool InputTextEnter(string label, ref string v, int maxLength);
    bool InputTextWithHint(string label, string hint, ref string v, int maxLength);
    bool Combo(string label, ref int index, string[] names);
    bool ColorEdit3(string label, ref Vector3 v);
    bool ColorEdit3Hdr(string label, ref Vector3 v);
    bool ColorEdit4(string label, ref Vector4 v);
    void Image(nint textureHandle, Vector2 size);
    void PlotLines(string label, float[] values, int count, string overlay, float min, float max, Vector2 size);

    bool TreeNode(string label);
    bool TreeNodeEx(string label, EditorTreeFlags flags);
    void TreePop();
    void BeginGroup();
    void EndGroup();
    float TreeNodeToLabelSpacing { get; }
    bool Selectable(string label, bool selected = false);
    bool Selectable(string label, bool selected, Vector2 size);
    bool SelectableRow(string label, bool selected);
    bool CollapsingHeader(string label);
    bool CollapsingHeader(string label, bool defaultOpen);
    bool CollapsingHeaderFramed(string label);
    bool CollapsingHeaderFramedOverlay(string label);
    bool BeginChild(string id, Vector2 size, bool border);
    bool BeginChild(string id, Vector2 size, bool border, bool horizontalScroll);
    bool BeginChildAutoResizeY(string id, bool border);
    void EndChild();
    bool BeginCombo(string label, string preview);
    void EndCombo();
    bool BeginPopup(string id);
    bool BeginPopupModalAutoResize(string id);
    void EndPopup();
    void OpenPopup(string id);
    bool BeginPopupContextItem(string id);
    void OpenPopupOnItemClick(string id);
    void CloseCurrentPopup();
    void SetNextWindowSizeAppearing(Vector2 size);
    bool BeginMenu(string label);
    void EndMenu();
    bool MenuItem(string label, bool enabled = true);
    bool MenuItem(string label, string shortcut, bool enabled = true);
    bool MenuItem(string label, string shortcut, bool selected, bool enabled);
    bool MenuItemToggle(string label, ref bool selected);

    bool FramedHeader(string label);

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
    void TableSetRowBgColor(uint color);

    bool TableGetSortSpec(out int column, out bool ascending);

    void Tooltip(string text);
    bool IsItemHovered();
    bool IsWindowHovered();
    bool IsWindowHoveredAllowBlocked();
    bool IsAnyItemHovered();
    bool IsMouseClicked(int button);
    void SetMouseCursorResizeEW();
    void CenterNextWindow(Vector2 size);
    void CenterNextWindowPos();
    bool IsMouseHoveringRect(Vector2 min, Vector2 max, bool clip = true);
    bool IsItemClicked();
    bool IsItemActive();
    bool IsItemActivated();
    bool IsItemFocused();
    bool IsItemDeactivatedAfterEdit();
    bool IsAnyItemActive();
    float TextLineHeightWithSpacing { get; }
    float FrameHeightWithSpacing { get; }

    Vector2 ItemRectMin { get; }
    Vector2 ItemRectMax { get; }
    Vector2 WindowPos { get; }
    Vector2 WindowSize { get; }
    void SetCursorScreenPos(Vector2 pos);
    bool IsWindowAppearing();
    void SetKeyboardFocusHere();
    bool KeyPressed(EditorGuiKey key);

    bool BeginDragDropTarget();
    int? AcceptDragDropPayloadInt(string type);
    string AcceptDragDropPayloadString(string type);
    void EndDragDropTarget();

    bool BeginDragDropSource();
    void SetDragDropPayloadInt(string type, int value);
    void SetDragDropPayloadString(string type, string value);
    void SetDragDropPayloadBytes(string type, byte[] payload);
    void EndDragDropSource();

    bool IsItemDeactivated();
    bool IsItemToggledOpen();
    bool IsWindowFocused();
    bool IsWindowFocusedIncludingChildren();
    bool IsMouseDoubleClicked(int button);
    void SetNextItemOpen(bool open);
    void SetNextItemOpenOnce(bool open);
    void SetNextItemOpenAlways(bool open);
    bool BeginPopupContextWindow(string id);
    bool BeginPopupContextWindowEmpty(string id);
    bool WantTextInput { get; }
    bool KeyCtrl { get; }
    bool KeyShift { get; }

    void PushFont(EditorFont font);
    void PopFont();
    float FontSize { get; }
    float FontSizeOf(EditorFont font);
    float TextLineHeight { get; }

    void PushColor(EditorStyleColor which, Vector4 rgba);
    void PopColor(int count = 1);
    void PushFramePadding(Vector2 padding);
    void PushItemSpacing(Vector2 spacing);
    void PushWindowPadding(Vector2 padding);
    void PushFrameRounding(float rounding);
    void PushAlphaScaled(float factor);
    void PushPopupBg(Vector4 rgba);
    void PopStyleVar(int count = 1);
    float FrameRounding { get; }
    float IndentSpacing { get; }
    float Alpha { get; }

    float WindowWidth { get; }
    float ScrollbarSize { get; }
    void SetClipboardText(string text);

    IEditorInput Input { get; }
    IEditorDrawList WindowDrawList { get; }
    uint ColorU32(Vector4 rgba);
    Vector4 StyleColor(EditorStyleColor which);
}
