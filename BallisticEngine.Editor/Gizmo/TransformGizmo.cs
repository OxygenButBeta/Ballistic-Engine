using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;

namespace BallisticEngine.Editor;

internal enum GizmoMode { Translate, Rotate, Scale }
internal enum GizmoSpace { World, Local }

internal enum GizmoPivot { Pivot, Center }

internal sealed class TransformGizmo {
    public GizmoMode Mode = GizmoMode.Translate;
    public GizmoSpace Space = GizmoSpace.World;
    public GizmoPivot Pivot = GizmoPivot.Pivot;

    Vector3[] currentAxes = Axes;

    int activeAxis = -1;
    bool dragging;
    Vector3 dragStartPosition;
    Quaternion dragStartRotation;
    Vector3 dragStartScale;
    float dragStartParam;
    Vector3 dragStartPlaneHit;
    Vector3 dragStartOrigin;
    float rotateAccum;
    SysVec2 lastMouse;

    bool vertexDragging;
    Vector3 vertexOffset;

    static float WrapAngle(float a) {
        while (a > MathF.PI) a -= MathF.Tau;
        while (a < -MathF.PI) a += MathF.Tau;
        return a;
    }

    static readonly Vector3[] Axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];

    readonly Vector3[] localAxesBuffer = new Vector3[3];
    Vector3[] LocalAxes(Quaternion rotation) {
        localAxesBuffer[0] = Vector3.Transform(Vector3.UnitX, rotation);
        localAxesBuffer[1] = Vector3.Transform(Vector3.UnitY, rotation);
        localAxesBuffer[2] = Vector3.Transform(Vector3.UnitZ, rotation);
        return localAxesBuffer;
    }

    static Vector3 SelectionCenter(Entity entity) {
        Vector3 min = entity.transform.WorldPosition, max = min;
        Scene scene = SceneManager.GetCurrentScene();
        if (scene is not null) {
            foreach (Entity e in scene.Entities) {
                if (e is null || e.IsDestroyed || ReferenceEquals(e, entity)) continue;
                if (!e.transform.IsDescendantOf(entity.transform)) continue;
                Vector3 p = e.transform.WorldPosition;
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }
        return (min + max) * 0.5f;
    }

    static bool SnapHeld => ImGui.GetIO().KeyCtrl;
    static float Snap(float value, float increment) =>
        increment > 0f ? MathF.Round(value / increment) * increment : value;
    static Vector3 SnapVector(Vector3 v, float increment) =>
        new(Snap(v.X, increment), Snap(v.Y, increment), Snap(v.Z, increment));

    static readonly (int a, int b, int normal)[] Planes = [(0, 1, 2), (0, 2, 1), (1, 2, 0)];

    static readonly uint[] AxisColors = [
        0xFF3A3ADD, 0xFF3ACC3A, 0xFFDD5A2A,
    ];
    const uint HighlightColor = 0xFF2AD4FF;

    public bool IsInteracting => dragging || vertexDragging;

    public bool IsHovered => activeAxis >= 0 || (Mode == GizmoMode.Translate && VertexSnap.Held && VertexSnap.Found);

    public void Draw(IViewProjectionProvider camera, Entity entity, SysVec2 viewMin, SysVec2 viewSize, bool viewHovered) {
        if (entity is null || viewSize.X < 2 || viewSize.Y < 2)
            return;

        Matrix4 vp = camera.GetViewMatrix() * camera.GetProjectionMatrix();
        Vector3 origin = Pivot == GizmoPivot.Center
            ? SelectionCenter(entity)
            : entity.transform.WorldMatrix.ExtractTranslation();

        if (!Project(origin, vp, viewMin, viewSize, out SysVec2 originPx))
            return;

        if (!dragging)
            currentAxes = (Space == GizmoSpace.Local || Mode == GizmoMode.Scale)
                ? LocalAxes(entity.transform.WorldRotation)
                : Axes;

        var camPos = camera.Transform.Position;
        float distance = Math.Max(0.01f, (origin - camPos).Length());
        float worldPerPixel = GizmoMath.WorldSizePerPixel(distance, viewSize.Y);
        float handleLength = EditorPrefs.Current.GizmoSize * worldPerPixel;

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        SysVec2 mouse = ImGui.GetMousePos();

        if (vertexDragging || (Mode == GizmoMode.Translate && VertexSnap.Held && !dragging)) {
            HandleVertexSnap(draw, entity, vp, viewMin, viewSize, mouse, viewHovered);
            return;
        }

        if (!dragging)
            activeAxis = viewHovered ? PickAxis(origin, handleLength, vp, viewMin, viewSize, mouse, originPx) : -1;

        HandleDrag(camera, entity, origin, handleLength, vp, viewMin, viewSize, mouse);

        switch (Mode) {
            case GizmoMode.Translate:
                DrawArrows(draw, origin, handleLength, vp, viewMin, viewSize, originPx);
                break;
            case GizmoMode.Rotate:
                DrawCircles(draw, origin, handleLength, vp, viewMin, viewSize, originPx);
                break;
            case GizmoMode.Scale:
                DrawScaleHandles(draw, origin, handleLength, vp, viewMin, viewSize, originPx);
                break;
        }
    }

    void HandleDrag(IViewProjectionProvider camera, Entity entity, Vector3 origin, float handleLength,
        Matrix4 vp, SysVec2 viewMin, SysVec2 viewSize, SysVec2 mouse) {
        if (!dragging) {
            if (activeAxis >= 0 && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) {
                string label = Mode switch {
                    GizmoMode.Translate => "Move",
                    GizmoMode.Rotate => "Rotate",
                    _ => "Scale",
                };
                EditorCommands.EditEntity(entity, label, () => { });
                dragging = true;
                dragStartPosition = entity.transform.WorldPosition;
                dragStartRotation = entity.transform.WorldRotation;
                dragStartScale = entity.transform.Scale;
                dragStartOrigin = origin;

                MouseRay(camera, vp, viewMin, viewSize, mouse, out Vector3 rayO, out Vector3 rayD);
                if (Mode == GizmoMode.Rotate) {
                    Project(origin, vp, viewMin, viewSize, out SysVec2 cpx);
                    dragStartParam = MathF.Atan2(mouse.Y - cpx.Y, mouse.X - cpx.X);
                    rotateAccum = 0f;
                    lastMouse = mouse;
                }
                else if (activeAxis >= 4) {
                    TryPointOnAxisPlane(origin, currentAxes[Planes[activeAxis - 4].normal], rayO, rayD,
                        out dragStartPlaneHit);
                }
                else if (activeAxis < 3) {
                    dragStartParam = ClosestParamOnAxis(origin, currentAxes[activeAxis], rayO, rayD);
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
                if (TryPointOnAxisPlane(dragStartOrigin, currentAxes[Planes[activeAxis - 4].normal], ro, rd,
                        out Vector3 hit)) {
                    Vector3 pos = dragStartPosition + (hit - dragStartPlaneHit);
                    entity.transform.WorldPosition = SnapHeld ? SnapVector(pos, EditorPrefs.Current.SnapMove) : pos;
                }
                break;
            }
            case GizmoMode.Translate when activeAxis < 3: {
                float t = ClosestParamOnAxis(dragStartOrigin, currentAxes[activeAxis], ro, rd);
                float moved = t - dragStartParam;
                if (SnapHeld) moved = Snap(moved, EditorPrefs.Current.SnapMove);
                entity.transform.WorldPosition = dragStartPosition + currentAxes[activeAxis] * moved;
                break;
            }
            case GizmoMode.Rotate when activeAxis == 8: {
                SysVec2 d = mouse - lastMouse;
                lastMouse = mouse;
                const float sens = 0.01f;
                Vector3 camUp = camera.Transform.Up;
                Vector3 camRight = camera.Transform.Right;
                Quaternion delta = Quaternion.CreateFromAxisAngle(camUp, d.X * sens) *
                                   Quaternion.CreateFromAxisAngle(camRight, d.Y * sens);
                entity.transform.WorldRotation = delta * entity.transform.WorldRotation;
                break;
            }
            case GizmoMode.Rotate: {
                Project(origin, vp, viewMin, viewSize, out SysVec2 cpx);
                float ang = MathF.Atan2(mouse.Y - cpx.Y, mouse.X - cpx.X);
                rotateAccum += WrapAngle(ang - dragStartParam);
                dragStartParam = ang;

                Vector3 axis = activeAxis == 7 ? camera.Transform.Forward : currentAxes[activeAxis];

                float facing = activeAxis == 7 ? 1f
                    : Vector3.Dot(axis, origin - camera.Transform.Position) > 0 ? 1f : -1f;
                float angle = -rotateAccum * facing;

                if (SnapHeld)
                    angle = MathHelper.DegreesToRadians(
                        Snap(MathHelper.RadiansToDegrees(angle), EditorPrefs.Current.SnapRotate));

                entity.transform.WorldRotation = Quaternion.CreateFromAxisAngle(axis.Normalized(), angle) * dragStartRotation;
                break;
            }
            case GizmoMode.Scale: {
                if (activeAxis == 3) {
                    float factor = 1f + (mouse.X - dragStartParam) * 0.01f;
                    Vector3 s = dragStartScale * Math.Max(0.01f, factor);
                    entity.transform.Scale = SnapHeld ? SnapVector(s, EditorPrefs.Current.SnapScale) : s;
                }
                else {
                    float t = ClosestParamOnAxis(dragStartOrigin, currentAxes[activeAxis], ro, rd);
                    float factor = Math.Max(0.01f, t / Math.Max(1e-5f, dragStartParam));
                    Vector3 s = dragStartScale;
                    s[activeAxis] *= factor;
                    if (SnapHeld) s[activeAxis] = Math.Max(0.01f, Snap(s[activeAxis], EditorPrefs.Current.SnapScale));
                    entity.transform.Scale = s;
                }
                break;
            }
        }
    }

    void HandleVertexSnap(ImDrawListPtr draw, Entity entity, Matrix4 vp, SysVec2 viewMin, SysVec2 viewSize,
        SysVec2 mouse, bool viewHovered) {
        VertexSnap.Solve(entity, vp, viewMin, viewSize, mouse);

        if (!vertexDragging) {
            if (viewHovered && VertexSnap.Found && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) {
                EditorCommands.EditEntity(entity, "Vertex Snap", () => { });
                vertexDragging = true;
                vertexOffset = VertexSnap.SourceWorld - entity.transform.WorldPosition;
            }
        }
        else {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left) || !VertexSnap.Held) {
                vertexDragging = false;
            }
            else if (VertexSnap.Found) {
                entity.transform.WorldPosition = VertexSnap.TargetWorld - vertexOffset;
            }
        }

        DrawVertexSnapMarkers(draw, vp, viewMin, viewSize);
    }

    void DrawVertexSnapMarkers(ImDrawListPtr draw, Matrix4 vp, SysVec2 viewMin, SysVec2 viewSize) {
        if (!VertexSnap.Found)
            return;

        if (Project(VertexSnap.SourceWorld, vp, viewMin, viewSize, out SysVec2 srcPx)) {
            draw.AddCircle(srcPx, 7f, HighlightColor, 12, 2f);
            draw.AddCircleFilled(srcPx, 2.5f, HighlightColor);
        }

        if (vertexDragging &&
            Project(VertexSnap.TargetWorld, vp, viewMin, viewSize, out SysVec2 dstPx)) {
            draw.AddCircleFilled(dstPx, 5f, 0xFF55FF55);
            draw.AddCircle(dstPx, 9f, 0xFF55FF55, 12, 1.5f);
        }
    }

    int PickAxis(Vector3 origin, float handleLength, Matrix4 vp, SysVec2 viewMin, SysVec2 viewSize,
        SysVec2 mouse, SysVec2 originPx) {
        const float threshold = 10f;

        if (Mode == GizmoMode.Scale &&
            Math.Abs(mouse.X - originPx.X) < 12f && Math.Abs(mouse.Y - originPx.Y) < 12f)
            return 3;

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
                for (var s = 0; s < 32; s++) {
                    Vector3 p = CirclePoint(origin, currentAxes[i], handleLength, s / 32f * MathF.Tau);
                    if (!Project(p, vp, viewMin, viewSize, out SysVec2 px))
                        continue;
                    var d = SysVec2.Distance(mouse, px);
                    if (d < bestDist) { bestDist = d; best = i; }
                }
            }
            else {
                if (!Project(origin + currentAxes[i] * handleLength, vp, viewMin, viewSize, out SysVec2 tip))
                    continue;
                var d = GizmoMath.DistanceToSegment(mouse, originPx, tip);
                if (d < bestDist) { bestDist = d; best = i; }
            }
        }

        if (Mode == GizmoMode.Rotate && best < 0) {
            float screenRadius = ScreenRadius(origin, handleLength, vp, viewMin, viewSize, originPx);
            float mouseDist = SysVec2.Distance(mouse, originPx);
            float viewRingR = screenRadius * 1.18f;
            if (Math.Abs(mouseDist - viewRingR) < threshold)
                return 7;
            if (mouseDist < screenRadius)
                return 8;
        }

        return best;
    }

    static float ScreenRadius(Vector3 origin, float handleLength, Matrix4 vp,
        SysVec2 viewMin, SysVec2 viewSize, SysVec2 originPx) {
        if (Project(origin + Vector3.UnitX * handleLength, vp, viewMin, viewSize, out SysVec2 edge))
            return SysVec2.Distance(originPx, edge);
        return handleLength;
    }

    void DrawArrows(ImDrawListPtr draw, Vector3 origin, float len, Matrix4 vp,
        SysVec2 viewMin, SysVec2 viewSize, SysVec2 originPx) {
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
            if (!Project(origin + currentAxes[i] * len, vp, viewMin, viewSize, out SysVec2 tip))
                continue;
            var color = activeAxis == i ? HighlightColor : AxisColors[i];
            draw.AddLine(originPx, tip, color, 3f);
            draw.AddCircleFilled(tip, 6f, color);
        }
        draw.AddCircleFilled(originPx, 4f, 0xFFCCCCCC);
    }

    bool ProjectPlaneQuad(Vector3 origin, int planeIndex, float len, Matrix4 vp,
        SysVec2 viewMin, SysVec2 viewSize, out SysVec2[] quad) {
        (int a, int b, _) = Planes[planeIndex];
        Vector3 va = currentAxes[a] * len;
        Vector3 vb = currentAxes[b] * len;

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
        SysVec2 viewMin, SysVec2 viewSize, SysVec2 originPx) {
        const int segments = 48;

        float screenRadius = ScreenRadius(origin, len, vp, viewMin, viewSize, originPx);

        draw.AddCircleFilled(originPx, screenRadius,
            activeAxis == 8 ? 0x33FFFFFFu : 0x18FFFFFFu);

        for (var i = 0; i < 3; i++) {
            var color = activeAxis == i ? HighlightColor : AxisColors[i];
            SysVec2 prev = default;
            var hasPrev = false;
            for (var s = 0; s <= segments; s++) {
                Vector3 p = CirclePoint(origin, currentAxes[i], len, s / (float)segments * MathF.Tau);
                if (!Project(p, vp, viewMin, viewSize, out SysVec2 px)) { hasPrev = false; continue; }
                if (hasPrev)
                    draw.AddLine(prev, px, color, activeAxis == i ? 3f : 2f);
                prev = px;
                hasPrev = true;
            }
        }

        var viewColor = activeAxis == 7 ? HighlightColor : 0xFFB0B0B0u;
        draw.AddCircle(originPx, screenRadius * 1.18f, viewColor, 64, activeAxis == 7 ? 3f : 1.6f);
    }

    void DrawScaleHandles(ImDrawListPtr draw, Vector3 origin, float len, Matrix4 vp,
        SysVec2 viewMin, SysVec2 viewSize, SysVec2 originPx) {
        for (var i = 0; i < 3; i++) {
            if (!Project(origin + currentAxes[i] * len, vp, viewMin, viewSize, out SysVec2 tip))
                continue;
            var color = activeAxis == i ? HighlightColor : AxisColors[i];
            draw.AddLine(originPx, tip, color, 3f);
            draw.AddRectFilled(tip - new SysVec2(5, 5), tip + new SysVec2(5, 5), color);
        }

        var center = activeAxis == 3 ? HighlightColor : 0xFFCCCCCCu;
        draw.AddRectFilled(originPx - new SysVec2(6, 6), originPx + new SysVec2(6, 6), center);
    }

    static Vector3 CirclePoint(Vector3 center, Vector3 axis, float radius, float angle) =>
        GizmoMath.CirclePoint(center, axis, radius, angle);

    static bool Project(Vector3 world, Matrix4 vp, SysVec2 viewMin, SysVec2 viewSize, out SysVec2 pixel) =>
        GizmoMath.Project(world, vp, viewMin, viewSize, out pixel);

    static void MouseRay(IViewProjectionProvider camera, Matrix4 vp, SysVec2 viewMin, SysVec2 viewSize,
        SysVec2 mouse, out Vector3 origin, out Vector3 direction) =>
        GizmoMath.MouseRay(vp, viewMin, viewSize, mouse, out origin, out direction);

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

    static bool TryPointOnAxisPlane(Vector3 planeOrigin, Vector3 axis, Vector3 rayO, Vector3 rayD,
        out Vector3 offset) {
        offset = Vector3.Zero;
        float denom = Vector3.Dot(rayD, axis);
        if (Math.Abs(denom) < 1e-6f)
            return false;
        float t = Vector3.Dot(planeOrigin - rayO, axis) / denom;
        if (t < 0)
            return false;
        offset = rayO + rayD * t - planeOrigin;
        return true;
    }

}
