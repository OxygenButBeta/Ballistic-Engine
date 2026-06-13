using Hexa.NET.ImGui;
using OpenTK.Mathematics;
using SysVec2 = System.Numerics.Vector2;

namespace BallisticEngine.Editor;

internal enum GizmoMode { Translate, Rotate, Scale }
internal enum GizmoSpace { World, Local }

// Where the gizmo SITS: at the entity's own transform pivot (origin), or at the CENTER of the
// selection's bounds — the AABB of the entity and all its descendants' world positions, so a
// hierarchy's handle sits in the middle of its parts (Unity's Pivot/Center toggle).
internal enum GizmoPivot { Pivot, Center }

// Hand-rolled transform gizmo drawn over the Scene view with the ImGui draw list.
// Translate: drag the X/Y/Z arrows. Rotate: drag the axis circles. Scale: drag the axis cubes,
// or the center square for uniform scale. Handles can use world or local (object) axes; holding
// Ctrl while dragging snaps to the increments in EditorPrefs.
internal sealed class TransformGizmo {
    public GizmoMode Mode = GizmoMode.Translate;
    public GizmoSpace Space = GizmoSpace.World;
    public GizmoPivot Pivot = GizmoPivot.Pivot;

    // The basis the gizmo uses this frame: world axes, or the entity's axes in Local space.
    // Scale ALWAYS uses local axes (scaling along world axes is meaningless for a rotated object).
    Vector3[] currentAxes = Axes;

    // Hover/drag state. Axis: 0=X 1=Y 2=Z, 3=uniform (scale center),
    // 4=XY plane (normal Z), 5=XZ plane (normal Y), 6=YZ plane (normal X).
    int activeAxis = -1;
    bool dragging;
    Vector3 dragStartPosition;
    Quaternion dragStartRotation;
    Vector3 dragStartScale;
    float dragStartParam;
    Vector3 dragStartPlaneHit;
    Vector3 dragStartOrigin;   // gizmo world origin captured at drag-start (fixed during the drag)
    float rotateAccum;
    SysVec2 lastMouse;

    // Vertex-snap drag state (hold V). vertexOffset is the fixed world-space vector from the entity's
    // pivot to the picked source vertex; during a pure translate it stays constant, so each frame the
    // new pivot is simply targetVertex - vertexOffset.
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

    // Center-mode gizmo position: the AABB centre of the entity's and all its descendants' world
    // positions. For a leaf entity this equals its pivot; for a hierarchy it sits in the middle of the
    // parts. (A render-bounds centre would need CPU mesh data per renderer; transform-position bounds
    // is cheap, stable, and matches "the centre of the objects inside" for authored hierarchies.)
    static Vector3 SelectionCenter(Entity entity) {
        Vector3 min = entity.transform.WorldPosition, max = min;
        Scene scene = SceneManager.GetCurrentScene();
        if (scene is not null) {
            // Transform has no child list; find descendants by walking the scene (IsDescendantOf).
            foreach (Entity e in scene.Entities) {
                if (e is null || e.IsDestroyed || ReferenceEquals(e, entity)) continue;
                if (!e.transform.IsDescendantOf(entity.transform)) continue;
                Vector3 p = e.transform.WorldPosition;
                min = Vector3.ComponentMin(min, p);
                max = Vector3.ComponentMax(max, p);
            }
        }
        return (min + max) * 0.5f;
    }

    // Snap helpers â€” active while Ctrl is held during a drag (increments from EditorPrefs).
    static bool SnapHeld => ImGui.GetIO().KeyCtrl;
    static float Snap(float value, float increment) =>
        increment > 0f ? MathF.Round(value / increment) * increment : value;
    static Vector3 SnapVector(Vector3 v, float increment) =>
        new(Snap(v.X, increment), Snap(v.Y, increment), Snap(v.Z, increment));

    // Plane handles: (first axis, second axis, normal axis index).
    static readonly (int a, int b, int normal)[] Planes = [(0, 1, 2), (0, 2, 1), (1, 2, 0)];

    static readonly uint[] AxisColors = [
        0xFF3A3ADD, // X red (ABGR)
        0xFF3ACC3A, // Y green
        0xFFDD5A2A, // Z blue
    ];
    const uint HighlightColor = 0xFF2AD4FF; // yellow-ish (ABGR)

    public bool IsInteracting => dragging || vertexDragging;

    // True when the mouse is over a handle this frame (axis hovered, or a vertex-snap source found) â€”
    // scene click-to-select uses this to avoid picking an object *through* a gizmo handle.
    public bool IsHovered => activeAxis >= 0 || (Mode == GizmoMode.Translate && VertexSnap.Held && VertexSnap.Found);

    public void Draw(IViewProjectionProvider camera, Entity entity, SysVec2 viewMin, SysVec2 viewSize, bool viewHovered) {
        if (entity is null || viewSize.X < 2 || viewSize.Y < 2)
            return;

        Matrix4 vp = camera.GetViewMatrix() * camera.GetProjectionMatrix();
        // Pivot mode: the entity's own origin. Center mode: the AABB centre of the entity + all its
        // descendants' world positions. Both track the entity as it moves, so a drag (which applies its
        // delta to WorldPosition, not to `origin`) stays consistent — the gizmo and pivot shift together.
        Vector3 origin = Pivot == GizmoPivot.Center
            ? SelectionCenter(entity)
            : entity.transform.WorldMatrix.ExtractTranslation();

        if (!Project(origin, vp, viewMin, viewSize, out SysVec2 originPx))
            return; // behind the camera

        // Pick the basis for this frame: Scale always uses the object's own axes; Translate/Rotate
        // follow the Local/World toggle. (Don't recompute mid-drag or the handle would jump.)
        if (!dragging)
            currentAxes = (Space == GizmoSpace.Local || Mode == GizmoMode.Scale)
                ? LocalAxes(entity.transform.WorldRotation)
                : Axes;

        // Constant on-screen gizmo size: scale the world-space handle length so its projection
        // stays ~Npx regardless of distance (N from EditorPrefs).
        var camPos = camera.Transform.Position;
        float distance = Math.Max(0.01f, (origin - camPos).Length);
        float worldPerPixel = GizmoMath.WorldSizePerPixel(distance, viewSize.Y);
        float handleLength = EditorPrefs.Current.GizmoSize * worldPerPixel;

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        SysVec2 mouse = ImGui.GetMousePos();

        // Vertex snapping (hold V in Translate mode, Unity-style): grab anywhere in the view and the
        // selection's nearest vertex welds to the nearest vertex under the cursor. Takes over the drag
        // entirely while armed (or mid vertex-drag), so the axis handles below are bypassed. Not armed
        // mid normal axis-drag, so tapping V during a regular move doesn't hijack it.
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

    // ---- Interaction ---------------------------------------------------------

    void HandleDrag(IViewProjectionProvider camera, Entity entity, Vector3 origin, float handleLength,
        Matrix4 vp, SysVec2 viewMin, SysVec2 viewSize, SysVec2 mouse) {
        if (!dragging) {
            if (activeAxis >= 0 && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) {
                EditorUndo.Push(Mode switch {   // snapshot before the gizmo mutates the transform
                    GizmoMode.Translate => "Move",
                    GizmoMode.Rotate => "Rotate",
                    _ => "Scale",
                });
                dragging = true;
                // Work in WORLD space so the gizmo is correct for parented objects (their local
                // Position/Rotation are relative to the parent; the handles live in world space).
                dragStartPosition = entity.transform.WorldPosition;
                dragStartRotation = entity.transform.WorldRotation;
                dragStartScale = entity.transform.Scale;
                dragStartOrigin = origin;   // freeze the origin so the axis line can't slide under the cursor

                MouseRay(camera, vp, viewMin, viewSize, mouse, out Vector3 rayO, out Vector3 rayD);
                if (Mode == GizmoMode.Rotate) {
                    // Screen-space rotation: remember the mouse angle around the projected gizmo
                    // center and the start point for the trackball. Stable from any view angle
                    // (the old 3D-plane method jumped when looking down the axis).
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
                // Hold position when the ray goes edge-on to the drag plane (no valid hit) instead of
                // snapping to a zero-delta jump.
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
                // Free trackball: horizontal mouse â†’ rotate about the camera's up, vertical â†’ about
                // the camera's right. Multi-axis rotation in one drag.
                SysVec2 d = mouse - lastMouse;
                lastMouse = mouse;
                const float sens = 0.01f;
                Vector3 camUp = camera.Transform.Up;
                Vector3 camRight = camera.Transform.Right;
                Quaternion delta = Quaternion.FromAxisAngle(camUp, d.X * sens) *
                                   Quaternion.FromAxisAngle(camRight, d.Y * sens);
                entity.transform.WorldRotation = delta * entity.transform.WorldRotation;
                break;
            }
            case GizmoMode.Rotate: {
                // Axis rings (0/1/2) and the view-facing ring (7): accumulate the per-frame change in
                // the mouse's angle around the projected gizmo center (accumulating, so rotations past
                // 180Â° don't wrap). Stable regardless of viewing angle.
                Project(origin, vp, viewMin, viewSize, out SysVec2 cpx);
                float ang = MathF.Atan2(mouse.Y - cpx.Y, mouse.X - cpx.X);
                rotateAccum += WrapAngle(ang - dragStartParam);
                dragStartParam = ang;

                Vector3 axis = activeAxis == 7 ? camera.Transform.Forward : currentAxes[activeAxis];

                // Flip so dragging clockwise on screen turns the object clockwise when the axis points
                // away from the camera (view ring needs no flip â€” it always faces us).
                float facing = activeAxis == 7 ? 1f
                    : Vector3.Dot(axis, origin - camera.Transform.Position) > 0 ? 1f : -1f;
                float angle = -rotateAccum * facing;

                if (SnapHeld)
                    angle = MathHelper.DegreesToRadians(
                        Snap(MathHelper.RadiansToDegrees(angle), EditorPrefs.Current.SnapRotate));

                entity.transform.WorldRotation = Quaternion.FromAxisAngle(axis.Normalized(), angle) * dragStartRotation;
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

    // Vertex snapping while V is held in Translate mode. Each frame we resolve the source vertex (on
    // the selection, nearest the cursor) and the target vertex (on any other mesh, nearest the cursor),
    // draw markers as feedback, and â€” once a drag is in progress â€” move the selection so the source
    // vertex lands on the target. Releasing the mouse (or letting go of V) ends the drag.
    void HandleVertexSnap(ImDrawListPtr draw, Entity entity, Matrix4 vp, SysVec2 viewMin, SysVec2 viewSize,
        SysVec2 mouse, bool viewHovered) {
        // Resolving against the *current* pose so the source vertex tracks the object as it moves.
        VertexSnap.Solve(entity, vp, viewMin, viewSize, mouse);

        if (!vertexDragging) {
            // Arm a drag: V is held, the view is hovered, a source vertex exists, and LMB just went down.
            if (viewHovered && VertexSnap.Found && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) {
                EditorUndo.Push("Vertex Snap");
                vertexDragging = true;
                // Freeze the pivot->source vertex offset; pure translation keeps it constant.
                vertexOffset = VertexSnap.SourceWorld - entity.transform.WorldPosition;
            }
        }
        else {
            // End conditions: mouse released, or V let go mid-drag.
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left) || !VertexSnap.Held) {
                vertexDragging = false;
            }
            else if (VertexSnap.Found) {
                // Move the pivot so the picked source vertex sits exactly on the target vertex.
                entity.transform.WorldPosition = VertexSnap.TargetWorld - vertexOffset;
            }
        }

        DrawVertexSnapMarkers(draw, vp, viewMin, viewSize);
    }

    // Visual feedback: a cyan ring on the source vertex (the one that will move) and, while dragging,
    // a filled marker on the target vertex it's snapping to.
    void DrawVertexSnapMarkers(ImDrawListPtr draw, Matrix4 vp, SysVec2 viewMin, SysVec2 viewSize) {
        if (!VertexSnap.Found)
            return;

        if (Project(VertexSnap.SourceWorld, vp, viewMin, viewSize, out SysVec2 srcPx)) {
            draw.AddCircle(srcPx, 7f, HighlightColor, 12, 2f);
            draw.AddCircleFilled(srcPx, 2.5f, HighlightColor);
        }

        if (vertexDragging &&
            Project(VertexSnap.TargetWorld, vp, viewMin, viewSize, out SysVec2 dstPx)) {
            draw.AddCircleFilled(dstPx, 5f, 0xFF55FF55);   // green target
            draw.AddCircle(dstPx, 9f, 0xFF55FF55, 12, 1.5f);
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

        // Rotate: the view-facing outer ring (7) and the free trackball interior (8). The axis rings
        // above win if the mouse is right on one; otherwise the outer ring / interior take over.
        if (Mode == GizmoMode.Rotate && best < 0) {
            float screenRadius = ScreenRadius(origin, handleLength, vp, viewMin, viewSize, originPx);
            float mouseDist = SysVec2.Distance(mouse, originPx);
            float viewRingR = screenRadius * 1.18f;
            if (Math.Abs(mouseDist - viewRingR) < threshold)
                return 7;                       // view-facing ring
            if (mouseDist < screenRadius)
                return 8;                       // free trackball interior
        }

        return best;
    }

    // Approximate on-screen radius of the rotation gizmo (used to place the view ring + trackball).
    static float ScreenRadius(Vector3 origin, float handleLength, Matrix4 vp,
        SysVec2 viewMin, SysVec2 viewSize, SysVec2 originPx) {
        // Project a point one handle-length along world X and measure the screen distance.
        if (Project(origin + Vector3.UnitX * handleLength, vp, viewMin, viewSize, out SysVec2 edge))
            return SysVec2.Distance(originPx, edge);
        return handleLength;
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
            if (!Project(origin + currentAxes[i] * len, vp, viewMin, viewSize, out SysVec2 tip))
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
        SysVec2 viewMin, SysVec2 viewSize, SysVec2 originPx) {
        const int segments = 48;

        float screenRadius = ScreenRadius(origin, len, vp, viewMin, viewSize, originPx);

        // Trackball interior: a faint filled disc you can grab to rotate freely (multi-axis).
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

        // View-facing outer ring (screen-space): rotates around the camera direction.
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

    // ---- Math ----------------------------------------------------------------
    // World<->screen projection, mouse rays, circle sampling and segment distance now live in the
    // shared GizmoMath so the transform handles, component gizmos, grid and orientation cube all
    // agree on the projection convention. The local helpers below are gizmo-specific.

    static Vector3 CirclePoint(Vector3 center, Vector3 axis, float radius, float angle) =>
        GizmoMath.CirclePoint(center, axis, radius, angle);

    static bool Project(Vector3 world, Matrix4 vp, SysVec2 viewMin, SysVec2 viewSize, out SysVec2 pixel) =>
        GizmoMath.Project(world, vp, viewMin, viewSize, out pixel);

    static void MouseRay(IViewProjectionProvider camera, Matrix4 vp, SysVec2 viewMin, SysVec2 viewSize,
        SysVec2 mouse, out Vector3 origin, out Vector3 direction) =>
        GizmoMath.MouseRay(vp, viewMin, viewSize, mouse, out origin, out direction);

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

    // Vector from the gizmo origin to where the mouse ray hits the axis-perpendicular plane. Returns
    // false (and a zero offset) when the ray is parallel to the plane (looking edge-on) or the hit is
    // behind the camera — callers must hold position rather than treat the zero as a real delta, or the
    // dragged object snaps to the origin the instant the view goes edge-on to the drag plane.
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
