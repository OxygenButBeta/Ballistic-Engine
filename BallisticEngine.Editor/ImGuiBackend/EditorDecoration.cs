using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

internal static class EditorDecoration {
    public static void DrawCard(SysVec2 min, SysVec2 max, float rounding = 6f) {
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, max, ImGui.GetColorU32(new SysVec4(1f, 1f, 1f, 0.035f)), rounding);
        draw.AddRect(min, max, ImGui.GetColorU32(BorderColor(0.55f)), rounding);
    }

    public static void DrawCardWithStripe(SysVec2 min, SysVec2 max, SysVec4 accent, float rounding = 6f) {
        DrawCard(min, max, rounding);
        DrawAccentStripe(min, max.Y - min.Y, accent);
    }

    public const float StripeWidth = 3f;
    public static void DrawAccentStripe(SysVec2 topLeft, float height, SysVec4 color) {
        ImGui.GetWindowDrawList().AddRectFilled(
            topLeft, new SysVec2(topLeft.X + StripeWidth, topLeft.Y + height), ImGui.GetColorU32(color));
    }

    public const float SectionPadY = 4f;
    public static void DrawSectionHeader(string label) {
        ImGui.Dummy(new SysVec2(0, SectionPadY));
        float contentRight = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        ImGui.PushFont(EditorTheme.Caption);
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.RowCaption);
        ImGui.TextUnformatted(label);
        ImGui.PopStyleColor();
        ImGui.PopFont();
        SysVec2 rectMin = ImGui.GetItemRectMin();
        SysVec2 rectMax = ImGui.GetItemRectMax();
        float midY = (rectMin.Y + rectMax.Y) * 0.5f;
        float x0 = rectMax.X + 8f;
        if (contentRight > x0 + 4f)
            ImGui.GetWindowDrawList().AddLine(
                new SysVec2(x0, midY), new SysVec2(contentRight, midY), ImGui.GetColorU32(BorderColor(1f)));
        ImGui.Dummy(new SysVec2(0, SectionPadY));
    }

    public static void DrawDivider(float padY = 3f) {
        ImGui.Dummy(new SysVec2(0, padY));
        SysVec2 p = ImGui.GetCursorScreenPos();
        float x0 = p.X;
        float x1 = p.X + ImGui.GetContentRegionAvail().X;
        ImGui.GetWindowDrawList().AddLine(
            new SysVec2(x0, p.Y), new SysVec2(x1, p.Y), ImGui.GetColorU32(BorderColor(1f)));
        ImGui.Dummy(new SysVec2(0, padY));
    }

    public static float DrawBadge(string text, SysVec4 color) {
        var draw = ImGui.GetWindowDrawList();
        SysVec2 ts = ImGui.CalcTextSize(text);
        float padX = 6f, padY = 2f;
        SysVec2 min = ImGui.GetCursorScreenPos();
        SysVec2 size = new(ts.X + padX * 2, ts.Y + padY * 2);
        SysVec2 max = min + size;
        draw.AddRectFilled(min, max, ImGui.GetColorU32(color), size.Y * 0.5f);
        draw.AddText(new SysVec2(min.X + padX, min.Y + padY), ImGui.GetColorU32(EditorTheme.Text), text);
        ImGui.Dummy(size);
        return size.X;
    }

    public static void DrawEmptyCard(SysVec2 min, SysVec2 max, float rounding = 8f) {
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, max, ImGui.GetColorU32(new SysVec4(1f, 1f, 1f, 0.012f)), rounding);
        draw.AddRect(min, max, ImGui.GetColorU32(BorderColor(0.7f)), rounding);
    }

    static SysVec4 BorderColor(float alpha) => new(
        EditorTheme.BorderLight.X, EditorTheme.BorderLight.Y, EditorTheme.BorderLight.Z, alpha);
}
