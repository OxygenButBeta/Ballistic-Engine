using Hexa.NET.ImGui;

namespace BallisticEngine.Editor;

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
