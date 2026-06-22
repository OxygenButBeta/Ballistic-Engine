using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

internal static class ViewportGrid {
    const int HalfLines = 20;

    public static void Draw(IViewProjectionProvider camera, SysVec2 viewMin, SysVec2 viewSize, float cellSize) {
        if (cellSize <= 0f)
            return;

        Matrix4 vp = camera.GetViewMatrix() * camera.GetProjectionMatrix();
        ImDrawListPtr draw = ImGui.GetWindowDrawList();

        Vector3 cam = camera.Transform.Position;
        float cx = MathF.Round(cam.X / cellSize) * cellSize;
        float cz = MathF.Round(cam.Z / cellSize) * cellSize;
        float extent = HalfLines * cellSize;

        int baseX = (int)MathF.Round(cx / cellSize);
        int baseZ = (int)MathF.Round(cz / cellSize);

        for (var i = -HalfLines; i <= HalfLines; i++) {
            float off = i * cellSize;
            float fade = 1f - MathF.Abs(i) / (float)HalfLines;

            int zKind = MathF.Abs(cx + off) < 1e-3f ? 1 : ((baseX + i) % 10 == 0 ? 3 : 0);
            Line(draw, vp, viewMin, viewSize,
                new Vector3(cx + off, 0, cz - extent), new Vector3(cx + off, 0, cz + extent), zKind, fade);

            int xKind = MathF.Abs(cz + off) < 1e-3f ? 2 : ((baseZ + i) % 10 == 0 ? 3 : 0);
            Line(draw, vp, viewMin, viewSize,
                new Vector3(cx - extent, 0, cz + off), new Vector3(cx + extent, 0, cz + off), xKind, fade);
        }
    }

    static void Line(ImDrawListPtr draw, Matrix4 vp, SysVec2 viewMin, SysVec2 viewSize,
        Vector3 a, Vector3 b, int kind, float fade) {
        if (!ClipToFrontOfCamera(ref a, ref b, vp))
            return;
        if (!GizmoMath.Project(a, vp, viewMin, viewSize, out SysVec2 pa) ||
            !GizmoMath.Project(b, vp, viewMin, viewSize, out SysVec2 pb))
            return;

        bool isAxis = kind is 1 or 2;
        SysVec4 color;
        float width;
        switch (kind) {
            case 1: color = new SysVec4(0.20f, 0.40f, 0.95f, 0.95f); width = 2.5f; break;
            case 2: color = new SysVec4(0.90f, 0.25f, 0.25f, 0.95f); width = 2.5f; break;
            case 3: color = new SysVec4(0.18f, 0.20f, 0.24f, 0.55f * (0.4f + 0.6f * fade)); width = 1.6f; break;
            default: color = new SysVec4(0.22f, 0.24f, 0.28f, 0.34f * (0.3f + 0.7f * fade)); width = 1f; break;
        }
        _ = isAxis;
        draw.AddLine(pa, pb, ImGui.GetColorU32(color), width);
    }

    static bool ClipToFrontOfCamera(ref Vector3 a, ref Vector3 b, Matrix4 vp) {
        const float eps = 0.001f;
        float wa = Clip(a, vp), wb = Clip(b, vp);

        bool aFront = wa > eps, bFront = wb > eps;
        if (!aFront && !bFront)
            return false;
        if (aFront && bFront)
            return true;

        float t = (eps - wa) / (wb - wa);
        Vector3 hit = a + (b - a) * t;
        if (aFront) b = hit; else a = hit;
        return true;
    }

    static float Clip(Vector3 world, Matrix4 vp) =>
        Vector4.Transform(new Vector4(world, 1f), vp).W;
}
