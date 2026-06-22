using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

internal sealed class GizmoDrawer : IGizmos {
    Matrix4 vp;
    SysVec2 viewMin;
    SysVec2 viewSize;
    ImDrawListPtr draw;

    public Vector3 Color { get; set; } = Vector3.One;
    public Vector3 CameraPosition { get; private set; }

    public void Begin(IViewProjectionProvider camera, SysVec2 min, SysVec2 size, ImDrawListPtr drawList) {
        vp = camera.GetViewMatrix() * camera.GetProjectionMatrix();
        CameraPosition = camera.Transform.Position;
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
        Vector4 ca = Vector4.Transform(new Vector4(from, 1f), vp);
        Vector4 cb = Vector4.Transform(new Vector4(to, 1f), vp);
        const float wEps = 1e-4f;
        bool aIn = ca.W > wEps, bIn = cb.W > wEps;
        if (!aIn && !bIn)
            return;
        if (aIn != bIn) {
            float t = (wEps - ca.W) / (cb.W - ca.W);
            Vector4 mid = ca + (cb - ca) * t;
            if (aIn) cb = mid; else ca = mid;
        }

        if (!ProjOcc(ca, out SysVec2 a, out bool oa) || !ProjOcc(cb, out SysVec2 b, out bool ob))
            return;
        if (!ClipToView(ref a, ref b))
            return;
        float alpha = (oa && ob) ? 0.28f : 0.9f;
        draw.AddLine(a, b, Col(alpha), 1.5f);
    }

    bool ProjOcc(Vector4 clip, out SysVec2 px, out bool occluded) {
        occluded = false;
        if (clip.W <= 1e-5f) { px = default; return false; }
        float ndcX = clip.X / clip.W, ndcY = clip.Y / clip.W;
        float wd = clip.Z / clip.W * 0.5f + 0.5f;
        px = new SysVec2(
            viewMin.X + (ndcX * 0.5f + 0.5f) * viewSize.X,
            viewMin.Y + (1f - (ndcY * 0.5f + 0.5f)) * viewSize.Y);
        if (GizmoDepthOcclusion.Enabled) {
            float u = (px.X - viewMin.X) / MathF.Max(1f, viewSize.X);
            float v = (px.Y - viewMin.Y) / MathF.Max(1f, viewSize.Y);
            occluded = wd > GizmoDepthOcclusion.SampleWindowDepth(u, v) + 0.0005f;
        }
        return true;
    }

    bool ClipToView(ref SysVec2 a, ref SysVec2 b) {
        float xMin = viewMin.X, yMin = viewMin.Y;
        float xMax = viewMin.X + viewSize.X, yMax = viewMin.Y + viewSize.Y;
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float t0 = 0f, t1 = 1f;
        Span<float> p = stackalloc float[] { -dx, dx, -dy, dy };
        Span<float> q = stackalloc float[] { a.X - xMin, xMax - a.X, a.Y - yMin, yMax - a.Y };
        for (var i = 0; i < 4; i++) {
            if (p[i] == 0f) {
                if (q[i] < 0f) return false;
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
        DrawCircle(center, Vector3.UnitX, radius);
        DrawCircle(center, Vector3.UnitY, radius);
        DrawCircle(center, Vector3.UnitZ, radius);
    }

    public void DrawSolidSphere(Vector3 center, float radius) {
        Vector4 cc = Vector4.Transform(new Vector4(center, 1f), vp);
        if (cc.W <= 1e-4f) return;
        if (!ProjOcc(cc, out SysVec2 c, out bool occ)) return;
        Vector3 toCam = CameraPosition - center;
        float tl = toCam.Length(); Vector3 viewDir = tl > 1e-5f ? toCam / tl : Vector3.UnitZ;
        Vector3 side = Vector3.Cross(viewDir, Vector3.UnitY);
        if (side.LengthSquared() < 1e-6f) side = Vector3.UnitX;
        side = Vector3.Normalize(side);
        float pr = 4f;
        if (P(center + side * radius, out SysVec2 e))
            pr = (e - c).Length();
        pr = Math.Clamp(pr, 2.5f, 9f);
        if (c.X + pr < viewMin.X || c.X - pr > viewMin.X + viewSize.X ||
            c.Y + pr < viewMin.Y || c.Y - pr > viewMin.Y + viewSize.Y) return;
        float alpha = occ ? 0.5f : 1.0f;
        draw.AddCircleFilled(c, pr, Col(alpha), 16);
        draw.AddCircle(c, pr, 0xC0000000u, 16, 1.2f);
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
        float height = direction.Length();
        if (height < 1e-4f)
            return;

        Vector3 dir = direction / height;
        float baseRadius = height * MathF.Tan(MathHelper.DegreesToRadians(halfAngleDegrees));
        Vector3 baseCenter = apex + dir * height;

        DrawCircle(baseCenter, dir, baseRadius);

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
