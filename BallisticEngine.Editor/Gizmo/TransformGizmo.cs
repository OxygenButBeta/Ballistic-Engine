using ImGuiNET;
using OpenTK.Mathematics;
using SysVec2 = System.Numerics.Vector2;

namespace BallisticEngine.Editor;

internal enum GizmoMode { Translate, Rotate, Scale }

// Hand-rolled transform gizmo drawn over the Scene view with the ImGui draw list.
// Translate: drag the X/Y/Z arrows (world axes). Rotate: drag the axis circles.
// Scale: drag the axis cubes, or the center square for uniform scale.
internal sealed class TransformGizmo {
    public GizmoMode Mode = GizmoMode.Translate;

    // Hover/drag state. Axis: 0=X 1=Y 2=Z, 3=uniform (scale center),
    // 4=XY plane (normal Z), 5=XZ plane (normal Y), 6=YZ plane (normal X).
    int activeAxis = -1;
    bool dragging;
    Vector3 dragStartPosition;
    Quaternion dragStartRotation;
    Vector3 dragStartScale;
    float dragStartParam;
    Vector3 dragStartRefDir;
    Vector3 dragStartPlaneHit;

    static readonly Vector3[] Axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];

    // Plane handles: (first axis, second axis, normal axis index).
    static readonly (int a, int b, int normal)[] Planes = [(0, 1, 2), (0, 2, 1), (1, 2, 0)];

    static readonly uint[] AxisColors = [
        0xFF3A3ADD, // X red (ABGR)
        0xFF3ACC3A, // Y green
        0xFFDD5A2A, // Z blue
    ];
    const uint HighlightColor = 0xFF2AD4FF; // yellow-ish (ABGR)

    public bool IsInteracting => dragging;

    public void Draw(IViewProjectionProvider camera, Entity entity, SysVec2 viewMin, SysVec2 viewSize, bool viewHovered) {
        if (entity is null || viewSize.X < 2 || viewSize.Y < 2)
            return;

        Matrix4 vp = camera.GetViewMatrix() * camera.GetProjectionMatrix();
        Vector3 origin = entity.transform.WorldMatrix.ExtractTranslation();

        if (!Project(origin, vp, viewMin, viewSize, out SysVec2 originPx))
            return; // behind the camera

        // Constant on-screen gizmo size: scale the world-space handle length so its projection
        // stays ~90px regardless of distance.
        var camPos = camera.Transform.Position;
        float distance = Math.Max(0.01f, (origin - camPos).Length);
        float worldPerPixel = WorldSizePerPixel(camera, distance, viewSize.Y);
        float handleLength = 90f * worldPerPixel;

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        SysVec2 mouse = ImGui.GetMousePos();

        if (!dragging)
            activeAxis = viewHovered ? PickAxis(origin, handleLength, vp, viewMin, viewSize, mouse, originPx) : -1;

        HandleDrag(camera, entity, origin, handleLength, vp, viewMin, viewSize, mouse);

        switch (Mode) {
            case GizmoMode.Translate:
                DrawArrows(draw, origin, handleLength, vp, viewMin, viewSize, originPx);
                break;
            case GizmoMode.Rotate:
                DrawCircles(draw, origin, handleLength, vp, viewMin, viewSize);
                break;
            case GizmoMode.Scale:
                DrawScaleHandles(draw, origin, handleLength, vp, viewMin, viewSize, originPx);
                break;
        }
    }

    // ---- Interaction ---------------------------------------------------------

    void HandleDrag(IViewProjectionProvider camera, Entity entity, Vector3 origin, float handleLength,
        Matrix4 vp, SysVec2 viewMin, SysVec2 viewSize, SysVec2 mouse) {
        if (!dragging) {
            if (activeAxis >= 0 && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) {
                EditorUndo.Push(); // snapshot before the gizmo mutates the transform
                dragging = true;
                dragStartPosition = entity.transform.Position;
                dragStartRotation = entity.transform.Rotation;
                dragStartScale = entity.transform.Scale;

                MouseRay(camera, vp, viewMin, viewSize, mouse, out Vector3 rayO, out Vector3 rayD);
                if (Mode == GizmoMode.Rotate && activeAxis < 3) {
                    dragStartRefDir = PointOnAxisPlane(origin, Axes[activeAxis], rayO, rayD);
                }
                else if (activeAxis >= 4) {
                    dragStartPlaneHit = PointOnAxisPlane(origin, Axes[Planes[activeAxis - 4].normal], rayO, rayD);
                }
                else if (activeAxis < 3) {
                    dragStartParam = ClosestParamOnAxis(origin, Axes[activeAxis], rayO, rayD);
                }
                else {
                    dragStartParam = mouse.X;
                }
            }
            return;
        }

        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) {
            dragging = false;
            return;
        }

        MouseRay(camera, vp, viewMin, viewSize, mouse, out Vector3 ro, out Vector3 rd);

        switch (Mode) {
            case GizmoMode.Translate when activeAxis >= 4: {
                Vector3 hit = PointOnAxisPlane(origin, Axes[Planes[activeAxis - 4].normal], ro, rd);
                entity.transform.Position = dragStartPosition + (hit - dragStartPlaneHit);
                break;
            }
            case GizmoMode.Translate when activeAxis < 3: {
                float t = ClosestParamOnAxis(origin, Axes[activeAxis], ro, rd);
                Vector3 delta = Axes[activeAxis] * (t - dragStartParam);
                entity.transform.Position = dragStartPosition + delta;
                break;
            }
            case GizmoMode.Rotate when activeAxis < 3: {
                Vector3 current = PointOnAxisPlane(origin, Axes[activeAxis], ro, rd);
                if (dragStartRefDir.LengthSquared < 1e-8f || current.LengthSquared < 1e-8f)
                    break;
                Vector3 a = dragStartRefDir.Normalized();
                Vector3 b = current.Normalized();
                float angle = MathF.Atan2(Vector3.Dot(Vector3.Cross(a, b), Axes[activeAxis]), Vector3.Dot(a, b));
                entity.transform.Rotation = Quaternion.FromAxisAngle(Axes[activeAxis], angle) * dragStartRotation;
                break;
            }
            case GizmoMode.Scale: {
                if (activeAxis == 3) {
                    float factor = 1f + (mouse.X - dragStartParam) * 0.01f;
                    entity.transform.Scale = dragStartScale * Math.Max(0.01f, factor);
                }
                else {
                    float t = ClosestParamOnAxis(origin, Axes[activeAxis], ro, rd);
                    float factor = Math.Max(0.01f, t / Math.Max(1e-5f, dragStartParam));
                    Vector3 s = dragStartScale;
                    s[activeAxis] *= factor;
                    entity.transform.Scale = s;
                }
                break;
            }
        }
    }

    int PickAxis(Vector3 origin, float handleLength, Matrix4 vp, SysVec2 viewMin, SysVec2 viewSize,
        SysVec2 mouse, SysVec2 originPx) {
        const float threshold = 10f;

        if (Mode == GizmoMode.Scale &&
            Math.Abs(mouse.X - originPx.X) < 12f && Math.Abs(mouse.Y - originPx.Y) < 12f)
            return 3;

        // Two-axis plane quads (translate only) take priority over single axes.
        if (Mode == GizmoMode.Translate) {
            for (var p = 0; p < Planes.Length; p++) {
                if (ProjectPlaneQuad(origin, p, handleLength, vp, viewMin, viewSize, out SysVec2[] quad) &&
                    PointInQuad(mouse, quad))
                    return 4 + p;
            }
        }

        var best = -1;
        var bestDist = threshold;

        for (var i = 0; i < 3; i++) {
            if (Mode == GizmoMode.Rotate) {
                // Distance to the projected circle: sample points.
                for (var s = 0; s < 32; s++) {
                    Vector3 p = CirclePoint(origin, Axes[i], handleLength, s / 32f * MathF.Tau);
                    if (!Project(p, vp, viewMin, viewSize, out SysVec2 px))
                        continue;
                    var d = SysVec2.Distance(mouse, px);
                    if (d < bestDist) { bestDist = d; best = i; }
                }
            }
            else {
                if (!Project(origin + Axes[i] * handleLength, vp, viewMin, viewSize, out SysVec2 tip))
                    continue;
                var d = DistanceToSegment(mouse, originPx, tip);
                if (d < bestDist) { bestDist = d; best = i; }
            }
        }

        return best;
    }

    // ---- Drawing -------------------------------------------------------------

    void DrawArrows(ImDrawListPtr draw, Vector3 origin, float len, Matrix4 vp,
        SysVec2 viewMin, SysVec2 viewSize, SysVec2 originPx) {
        // Plane quads first (under the axis lines).
        for (var p = 0; p < Planes.Length; p++) {
            if (!ProjectPlaneQuad(origin, p, len, vp, viewMin, viewSize, out SysVec2[] quad))
                continue;
            var baseColor = AxisColors[Planes[p].normal];
            var fill = activeAxis == 4 + p ? (HighlightColor & 0x00FFFFFF) | 0x88000000 : (baseColor & 0x00FFFFFF) | 0x55000000;
            draw.AddQuadFilled(quad[0], quad[1], quad[2], quad[3], fill);
            draw.AddQuad(quad[0], quad[1], quad[2], quad[3],
                activeAxis == 4 + p ? HighlightColor : baseColor, 1.5f);
        }

        for (var i = 0; i < 3; i++) {
            if (!Project(origin + Axes[i] * len, vp, viewMin, viewSize, out SysVec2 tip))
                continue;
            var color = activeAxis == i ? HighlightColor : AxisColors[i];
            draw.AddLine(originPx, tip, color, 3f);
            draw.AddCircleFilled(tip, 6f, color);
        }
        draw.AddCircleFilled(originPx, 4f, 0xFFCCCCCC);
    }

    // The quad sits between 30% and 60% of the handle length along the plane's two axes.
    bool ProjectPlaneQuad(Vector3 origin, int planeIndex, float len, Matrix4 vp,
        SysVec2 viewMin, SysVec2 viewSize, out SysVec2[] quad) {
        (int a, int b, _) = Planes[planeIndex];
        Vector3 va = Axes[a] * len;
        Vector3 vb = Axes[b] * len;

        quad = new SysVec2[4];
        Vector3[] corners = [
            origin + va * 0.3f + vb * 0.3f,
            origin + va * 0.6f + vb * 0.3f,
            origin + va * 0.6f + vb * 0.6f,
            origin + va * 0.3f + vb * 0.6f,
        ];

        for (var i = 0; i < 4; i++) {
            if (!Project(corners[i], vp, viewMin, viewSize, out quad[i]))
                return false;
        }

        return true;
    }

    static bool PointInQuad(SysVec2 p, SysVec2[] quad) {
        // Convex polygon containment: consistent cross-product signs.
        var sign = 0;
        for (var i = 0; i < 4; i++) {
            SysVec2 a = quad[i];
            SysVec2 b = quad[(i + 1) % 4];
            float cross = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
            var s = cross > 0 ? 1 : cross < 0 ? -1 : 0;
            if (s == 0)
                continue;
            if (sign == 0)
                sign = s;
            else if (sign != s)
                return false;
        }
        return true;
    }

    void DrawCircles(ImDrawListPtr draw, Vector3 origin, float len, Matrix4 vp,
        SysVec2 viewMin, SysVec2 viewSize) {
        const int segments = 48;
        for (var i = 0; i < 3; i++) {
            var color = activeAxis == i ? HighlightColor : AxisColors[i];
            SysVec2 prev = default;
            var hasPrev = false;
            for (var s = 0; s <= segments; s++) {
                Vector3 p = CirclePoint(origin, Axes[i], len, s / (float)segments * MathF.Tau);
                if (!Project(p, vp, viewMin, viewSize, out SysVec2 px)) { hasPrev = false; continue; }
                if (hasPrev)
                    draw.AddLine(prev, px, color, activeAxis == i ? 3f : 2f);
                prev = px;
                hasPrev = true;
            }
        }
    }

    void DrawScaleHandles(ImDrawListPtr draw, Vector3 origin, float len, Matrix4 vp,
        SysVec2 viewMin, SysVec2 viewSize, SysVec2 originPx) {
        for (var i = 0; i < 3; i++) {
            if (!Project(origin + Axes[i] * len, vp, viewMin, viewSize, out SysVec2 tip))
                continue;
            var color = activeAxis == i ? HighlightColor : AxisColors[i];
            draw.AddLine(originPx, tip, color, 3f);
            draw.AddRectFilled(tip - new SysVec2(5, 5), tip + new SysVec2(5, 5), color);
        }

        var center = activeAxis == 3 ? HighlightColor : 0xFFCCCCCCu;
        draw.AddRectFilled(originPx - new SysVec2(6, 6), originPx + new SysVec2(6, 6), center);
    }

    // ---- Math ----------------------------------------------------------------

    static Vector3 CirclePoint(Vector3 center, Vector3 axis, float radius, float angle) {
        Vector3 u = Vector3.Cross(axis, Math.Abs(axis.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY).Normalized();
        Vector3 v = Vector3.Cross(axis, u);
        return center + (u * MathF.Cos(angle) + v * MathF.Sin(angle)) * radius;
    }

    static bool Project(Vector3 world, Matrix4 vp, SysVec2 viewMin, SysVec2 viewSize, out SysVec2 pixel) {
        Vector4 clip = Vector4.TransformRow(new Vector4(world, 1f), vp);
        if (clip.W <= 1e-5f) {
            pixel = default;
            return false;
        }

        var ndcX = clip.X / clip.W;
        var ndcY = clip.Y / clip.W;
        pixel = new SysVec2(
            viewMin.X + (ndcX * 0.5f + 0.5f) * viewSize.X,
            viewMin.Y + (1f - (ndcY * 0.5f + 0.5f)) * viewSize.Y);
        return true;
    }

    static void MouseRay(IViewProjectionProvider camera, Matrix4 vp, SysVec2 viewMin, SysVec2 viewSize,
        SysVec2 mouse, out Vector3 origin, out Vector3 direction) {
        var ndc = new Vector2(
            (mouse.X - viewMin.X) / viewSize.X * 2f - 1f,
            (1f - (mouse.Y - viewMin.Y) / viewSize.Y) * 2f - 1f);

        Matrix4 inverse = Matrix4.Invert(vp);
        Vector4 near = Vector4.TransformRow(new Vector4(ndc.X, ndc.Y, -1f, 1f), inverse);
        Vector4 far = Vector4.TransformRow(new Vector4(ndc.X, ndc.Y, 1f, 1f), inverse);

        origin = near.Xyz / near.W;
        direction = (far.Xyz / far.W - origin).Normalized();
    }

    // Parameter t along the axis (origin + axis*t) closest to the mouse ray.
    static float ClosestParamOnAxis(Vector3 axisOrigin, Vector3 axisDir, Vector3 rayO, Vector3 rayD) {
        Vector3 w0 = axisOrigin - rayO;
        float a = Vector3.Dot(axisDir, axisDir);
        float b = Vector3.Dot(axisDir, rayD);
        float c = Vector3.Dot(rayD, rayD);
        float d = Vector3.Dot(axisDir, w0);
        float e = Vector3.Dot(rayD, w0);
        float denominator = a * c - b * b;
        if (Math.Abs(denominator) < 1e-6f)
            return 0f;
        return (b * e - c * d) / denominator;
    }

    // Vector from the gizmo origin to where the mouse ray hits the axis-perpendicular plane.
    static Vector3 PointOnAxisPlane(Vector3 planeOrigin, Vector3 axis, Vector3 rayO, Vector3 rayD) {
        float denom = Vector3.Dot(rayD, axis);
        if (Math.Abs(denom) < 1e-6f)
            return Vector3.Zero;
        float t = Vector3.Dot(planeOrigin - rayO, axis) / denom;
        return t < 0 ? Vector3.Zero : rayO + rayD * t - planeOrigin;
    }

    static float WorldSizePerPixel(IViewProjectionProvider camera, float distance, float viewHeightPx) {
        // 45° vertical fov: world height at distance = 2*d*tan(fov/2).
        var worldHeight = 2f * distance * MathF.Tan(MathHelper.DegreesToRadians(45f) * 0.5f);
        return worldHeight / Math.Max(1f, viewHeightPx);
    }

    static float DistanceToSegment(SysVec2 p, SysVec2 a, SysVec2 b) {
        SysVec2 ab = b - a;
        float lengthSq = ab.LengthSquared();
        if (lengthSq < 1e-5f)
            return SysVec2.Distance(p, a);
        float t = Math.Clamp(SysVec2.Dot(p - a, ab) / lengthSq, 0f, 1f);
        return SysVec2.Distance(p, a + ab * t);
    }
}
