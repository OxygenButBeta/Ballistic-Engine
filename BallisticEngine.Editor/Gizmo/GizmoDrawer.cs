using Hexa.NET.ImGui;
using OpenTK.Mathematics;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

// Editor-side implementation of the engine's IGizmos: projects the world-space primitives a
// component requests (in OnDrawGizmos/OnDrawGizmosSelected) through the editor camera and paints
// them with the ImGui draw list. This is the ONLY place component gizmos touch ImGui â€” the engine
// side stays renderer/UI-free. Begin() is called once per frame with the current camera + viewport.
internal sealed class GizmoDrawer : IGizmos {
    Matrix4 vp;
    SysVec2 viewMin;
    SysVec2 viewSize;
    ImDrawListPtr draw;

    public Vector3 Color { get; set; } = Vector3.One;

    public void Begin(IViewProjectionProvider camera, SysVec2 min, SysVec2 size, ImDrawListPtr drawList) {
        vp = camera.GetViewMatrix() * camera.GetProjectionMatrix();
        viewMin = min;
        viewSize = size;
        draw = drawList;
        Color = Vector3.One;
    }

    uint Col(float alpha = 1f) =>
        ImGui.GetColorU32(new SysVec4(Color.X, Color.Y, Color.Z, alpha));

    bool P(Vector3 world, out SysVec2 px) =>
        GizmoMath.Project(world, vp, viewMin, viewSize, out px);

    public void DrawLine(Vector3 from, Vector3 to) {
        if (P(from, out SysVec2 a) && P(to, out SysVec2 b) && ClipToView(ref a, ref b))
            draw.AddLine(a, b, Col(), 1.5f);
    }

    // Liang-Barsky clip of a screen-space segment to the Scene-view rect. Project() returns true for
    // any point in front of the camera even if its PIXEL lands outside the viewport, so without this a
    // gizmo line that runs off-screen bleeds across the toolbar/tabs/other panels. Clipping the segment
    // to [viewMin, viewMin+viewSize] keeps every gizmo inside the Scene image. Returns false if the
    // segment is fully outside.
    bool ClipToView(ref SysVec2 a, ref SysVec2 b) {
        float xMin = viewMin.X, yMin = viewMin.Y;
        float xMax = viewMin.X + viewSize.X, yMax = viewMin.Y + viewSize.Y;
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float t0 = 0f, t1 = 1f;
        Span<float> p = stackalloc float[] { -dx, dx, -dy, dy };
        Span<float> q = stackalloc float[] { a.X - xMin, xMax - a.X, a.Y - yMin, yMax - a.Y };
        for (var i = 0; i < 4; i++) {
            if (p[i] == 0f) {
                if (q[i] < 0f) return false;          // parallel and outside this edge
            }
            else {
                float r = q[i] / p[i];
                if (p[i] < 0f) { if (r > t1) return false; if (r > t0) t0 = r; }
                else { if (r < t0) return false; if (r < t1) t1 = r; }
            }
        }
        var na = new SysVec2(a.X + t0 * dx, a.Y + t0 * dy);
        var nb = new SysVec2(a.X + t1 * dx, a.Y + t1 * dy);
        a = na; b = nb;
        return true;
    }

    public void DrawRay(Vector3 origin, Vector3 direction) => DrawLine(origin, origin + direction);

    public void DrawWireSphere(Vector3 center, float radius) {
        // Three orthogonal great circles read as a sphere from any angle.
        DrawCircle(center, Vector3.UnitX, radius);
        DrawCircle(center, Vector3.UnitY, radius);
        DrawCircle(center, Vector3.UnitZ, radius);
    }

    void DrawCircle(Vector3 center, Vector3 axis, float radius) {
        const int segments = 40;
        SysVec2 prev = default;
        var hasPrev = false;
        for (var s = 0; s <= segments; s++) {
            Vector3 p = GizmoMath.CirclePoint(center, axis, radius, s / (float)segments * MathF.Tau);
            if (!P(p, out SysVec2 px)) { hasPrev = false; continue; }
            if (hasPrev)
                draw.AddLine(prev, px, Col(0.9f), 1.3f);
            prev = px;
            hasPrev = true;
        }
    }

    public void DrawWireCone(Vector3 apex, Vector3 direction, float halfAngleDegrees) {
        float height = direction.Length;
        if (height < 1e-4f)
            return;

        Vector3 dir = direction / height;
        float baseRadius = height * MathF.Tan(MathHelper.DegreesToRadians(halfAngleDegrees));
        Vector3 baseCenter = apex + dir * height;

        DrawCircle(baseCenter, dir, baseRadius);

        // Four edges from the apex to the base circle (at 0/90/180/270 degrees).
        for (var s = 0; s < 4; s++) {
            Vector3 rim = GizmoMath.CirclePoint(baseCenter, dir, baseRadius, s / 4f * MathF.Tau);
            DrawLine(apex, rim);
        }
    }

    public void DrawWireCube(Vector3 center, Vector3 size, Quaternion rotation) {
        Vector3 h = size * 0.5f;
        Span<Vector3> c = stackalloc Vector3[8];
        var k = 0;
        for (var xi = -1; xi <= 1; xi += 2)
            for (var yi = -1; yi <= 1; yi += 2)
                for (var zi = -1; zi <= 1; zi += 2)
                    c[k++] = center + Vector3.Transform(new Vector3(h.X * xi, h.Y * yi, h.Z * zi), rotation);

        // Bit-pattern corner ordering (x,y,z) â€” connect pairs differing in exactly one axis.
        for (var i = 0; i < 8; i++)
            for (var j = i + 1; j < 8; j++)
                if (System.Numerics.BitOperations.PopCount((uint)(i ^ j)) == 1)
                    DrawLine(c[i], c[j]);
    }

    public void DrawIcon(Vector3 center, GizmoIcon icon) {
        if (!P(center, out SysVec2 px))
            return;

        uint color = Col();
        switch (icon) {
            case GizmoIcon.Light:
                draw.AddCircleFilled(px, 5f, color);
                // Little rays around the bulb.
                for (var s = 0; s < 8; s++) {
                    float a = s / 8f * MathF.Tau;
                    var d = new SysVec2(MathF.Cos(a), MathF.Sin(a));
                    draw.AddLine(px + d * 7f, px + d * 11f, color, 1.2f);
                }
                break;
            case GizmoIcon.Camera:
                draw.AddRect(px - new SysVec2(7, 5), px + new SysVec2(7, 5), color, 1f, ImDrawFlags.None, 1.5f);
                draw.AddTriangleFilled(px + new SysVec2(7, -4), px + new SysVec2(7, 4), px + new SysVec2(13, 0), color);
                break;
        }
    }
}
