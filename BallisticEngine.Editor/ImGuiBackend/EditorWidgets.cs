using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

internal static partial class EditorWidgets {
    public static bool ToggleSwitch(string id, ref bool value, float scale) {
        float h = ImGui.GetFrameHeight() * 0.82f;
        float w = h * 1.85f;
        SysVec2 pos = ImGui.GetCursorScreenPos();
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

        float knobR = r - 2.5f * scale;
        float knobX = value ? max.X - r : min.X + r;
        SysVec2 knob = new(knobX, min.Y + r);
        draw.AddCircleFilled(knob, knobR, ImGui.GetColorU32(new SysVec4(1, 1, 1, value ? 0.98f : 0.78f)));

        return clicked;
    }

    public static bool SearchField(string id, string hint, ref string buffer, float width = -1f, uint maxLen = 128) {
        ImGui.SetNextItemWidth(width);
        return ImGui.InputTextWithHint(id, $"{EditorIcons.Search} {hint}", ref buffer, maxLen);
    }

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
