using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;

namespace BallisticEngine.Editor;

internal static class OrientationGizmo {
    static readonly (Vector3 dir, string label, uint color)[] AxesInfo = [
        (Vector3.UnitX, "X", 0xFF4040E0),
        (Vector3.UnitY, "Y", 0xFF40C040),
        (Vector3.UnitZ, "Z", 0xFFE08040),
    ];

    public static void Draw(EditorCamera camera, SysVec2 viewMin, SysVec2 viewSize, float scale, bool viewHovered) {
        if (viewSize.X < 60 || viewSize.Y < 60)
            return;

        float radius = 34 * scale;
        var center = new SysVec2(viewMin.X + viewSize.X - radius - 14 * scale, viewMin.Y + radius + 14 * scale);
        ImDrawListPtr draw = ImGui.GetWindowDrawList();

        Quaternion inv = camera.Transform.Rotation;
        inv = Quaternion.Inverse(inv);

        SysVec2 mouse = ImGui.GetMousePos();
        var hovered = viewHovered && (mouse - center).LengthSquared() < (radius + 8 * scale) * (radius + 8 * scale);

        var balls = new (SysVec2 pos, float depth, uint color, string label, Vector3 snapDir, bool positive)[6];
        var n = 0;
        foreach ((Vector3 dir, string label, uint color) in AxesInfo) {
            balls[n++] = Ball(dir, label, color, center, radius, inv, positive: true);
            balls[n++] = Ball(-dir, label, color, center, radius, inv, positive: false);
        }

        Array.Sort(balls, (a, b) => a.depth.CompareTo(b.depth));

        var clickedDir = Vector3.Zero;
        var clicked = false;

        foreach (var b in balls)
            if (b.positive)
                draw.AddLine(center, b.pos, (b.color & 0x00FFFFFF) | 0x90000000, 2f);

        float ballR = 9 * scale;
        foreach (var b in balls) {
            var over = hovered && (mouse - b.pos).LengthSquared() < ballR * ballR;
            uint fill = b.positive || over ? b.color : (b.color & 0x00FFFFFF) | 0x40000000;
            draw.AddCircleFilled(b.pos, ballR, fill);
            draw.AddCircle(b.pos, ballR, (b.color & 0x00FFFFFF) | 0xFF000000, 0, 1.5f);
            if (b.positive)
                draw.AddText(b.pos - new SysVec2(4 * scale, 7 * scale), 0xFF202020, b.label);

            if (over && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) {
                clicked = true;
                clickedDir = b.snapDir;
            }
        }

        if (clicked)
            camera.LookDirection(clickedDir);
    }

    static (SysVec2, float, uint, string, Vector3, bool) Ball(Vector3 dir, string label, uint color,
        SysVec2 center, float radius, Quaternion invRotation, bool positive) {
        Vector3 v = Vector3.Transform(dir, invRotation);
        var pos = new SysVec2(center.X + v.X * radius, center.Y - v.Y * radius);
        Vector3 snap = -dir;
        return (pos, v.Z, color, label, snap, positive);
    }
}
