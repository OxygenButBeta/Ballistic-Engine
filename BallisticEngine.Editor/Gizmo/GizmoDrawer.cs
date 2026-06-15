using Hexa.NET.ImGui;
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
        // Clip the segment against the NEAR PLANE in clip space FIRST. A point in front of the camera
        // (w>0) but far outside the frustum sides still projects to an enormous pixel coordinate; before
        // this, ClipToView would trim that garbage point to the viewport EDGE and draw a spurious line
        // sweeping across the Scene — the "gizmos explode into a spiderweb while moving" bug (a probe
        // marker swinging past the camera as you fly). Near-clipping the 3D segment keeps both projected
        // endpoints finite and on the correct side, so the cross marker stays a small cross.
        Vector4 ca = Vector4.Transform(new Vector4(from, 1f), vp);
        Vector4 cb = Vector4.Transform(new Vector4(to, 1f), vp);
        const float wEps = 1e-4f;
        bool aIn = ca.W > wEps, bIn = cb.W > wEps;
        if (!aIn && !bIn)
            return;                       // whole segment behind the camera
        if (aIn != bIn) {
            // One endpoint behind: move it to the near plane (w = wEps) along the segment.
            float t = (wEps - ca.W) / (cb.W - ca.W);
            Vector4 mid = ca + (cb - ca) * t;
            if (aIn) cb = mid; else ca = mid;
        }

        if (!ProjOcc(ca, out SysVec2 a, out bool oa) || !ProjOcc(cb, out SysVec2 b, out bool ob))
            return;
        if (!ClipToView(ref a, ref b))
            return;
        // Dim when BOTH endpoints are occluded (a segment straddling an edge stays bright so silhouettes
        // read). Behind-geometry gizmos draw faint so you can tell they're behind a wall, not in front.
        float alpha = (oa && ob) ? 0.28f : 0.9f;
        draw.AddLine(a, b, Col(alpha), 1.5f);
    }

    // Project an ALREADY clip-space point (post near-clip, so w>0) to a pixel + occlusion flag.
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
        float height = direction.Length();
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
