using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

// Custom draw-list widgets that give the editor a purpose-built, modern feel beyond stock ImGui:
// an iOS/Material-style toggle switch and a soft drop-shadow helper for floating cards. All are
// theme-aware (read the accent from the current style) and DPI-aware (pass the UI scale).
internal static partial class EditorWidgets {
    // A sliding on/off switch. Returns true the frame it changes. Reads better than a checkbox for
    // standalone booleans (panel headers, settings, the live-refresh toggle).
    public static bool ToggleSwitch(string id, ref bool value, float scale) {
        float h = ImGui.GetFrameHeight() * 0.82f;
        float w = h * 1.85f;
        SysVec2 pos = ImGui.GetCursorScreenPos();
        // Center vertically on the row.
        float yPad = (ImGui.GetFrameHeight() - h) * 0.5f;
        pos.Y += yPad;

        ImGui.InvisibleButton(id, new SysVec2(w, h + yPad * 2));
        bool hovered = ImGui.IsItemHovered();
        bool clicked = ImGui.IsItemClicked();
        if (clicked)
            value = !value;

        var draw = ImGui.GetWindowDrawList();
        var c = ImGui.GetStyle().Colors;
        SysVec4 accent = c[(int)ImGuiCol.CheckMark];
        SysVec4 offTrack = c[(int)ImGuiCol.FrameBg];
        SysVec4 offTrackHover = c[(int)ImGuiCol.FrameBgHovered];

        float r = h * 0.5f;
        SysVec2 min = pos;
        SysVec2 max = pos + new SysVec2(w, h);
        uint track = ImGui.GetColorU32(value ? accent : (hovered ? offTrackHover : offTrack));
        draw.AddRectFilled(min, max, track, r);

        // Knob slides left↔right.
        float knobR = r - 2.5f * scale;
        float knobX = value ? max.X - r : min.X + r;
        SysVec2 knob = new(knobX, min.Y + r);
        draw.AddCircleFilled(knob, knobR, ImGui.GetColorU32(new SysVec4(1, 1, 1, value ? 0.98f : 0.78f)));

        return clicked;
    }

    // EF10a — the ONE reusable search-field primitive. A single inline InputTextWithHint with the lucide
    // search glyph in the hint, stretched to `width` (-1 = fill). Returns true the frame the text changes.
    // Factored here (vs the ~half-dozen sites that inline their own InputTextWithHint — Add-Component,
    // asset picker, feature search) so Hierarchy/Assets/Add-Component can later adopt ONE styled field; the
    // first consumer is the per-component member search (EF10a). NOTE: the Hexa managed ref-string overload
    // must NOT pass EnterReturnsTrue — that flag defers the buffer write-back until Enter, so live typing
    // wouldn't filter; callers that want Enter detect it separately (IsItemFocused + IsKeyPressed).
    public static bool SearchField(string id, string hint, ref string buffer, float width = -1f, uint maxLen = 128) {
        ImGui.SetNextItemWidth(width);
        return ImGui.InputTextWithHint(id, $"{EditorIcons.Search} {hint}", ref buffer, maxLen);
    }

    // Draws a soft drop shadow just under a rectangle (call before drawing the card itself) so
    // popups/cards lift off the near-black background. Uses concentric fading rounded rects.
    public static void DropShadow(ImDrawListPtr draw, SysVec2 min, SysVec2 max, float rounding, float scale) {
        int layers = 6;
        float spread = 7f * scale;
        for (var i = layers; i >= 1; i--) {
            float t = i / (float)layers;
            float grow = spread * t;
            float alpha = 0.04f * (1f - t) + 0.015f;
            uint col = ImGui.GetColorU32(new SysVec4(0, 0, 0, alpha));
            draw.AddRectFilled(min - new SysVec2(grow, grow - 1f * scale),
                               max + new SysVec2(grow, grow + 2f * scale),
                               col, rounding + grow);
        }
    }
}
