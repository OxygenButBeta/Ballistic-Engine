using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

// Scene-view drag handles for a selected entity's WheelColliders (Unity's WheelCollider edit handles).
// Two square handles per wheel, both dragged along the wheel's UP axis so the values match the
// inspector exactly:
//   * RADIUS  — a square at the top of the wheel circle; drag away from the centre to grow the radius.
//   * TRAVEL  — a square at full droop (mount − up·travel); drag down to lengthen the suspension travel.
// The drawing (the circle + travel line) is the WheelCollider's own OnDrawGizmosSelected; this file
// only adds the interactive squares. Undo snapshots on grab, like ColliderHandles.
internal static class WheelHandles {
    static WheelCollider activeWheel;
    static int activeHandle = -1;          // 0 = radius, 1 = travel
    static Vector3 grabAnchor, grabDir;
    static float grabParam, grabValue;

    public static bool IsInteracting => activeWheel is not null;

    // Returns true when a wheel changed this frame (caller marks the scene dirty).
    public static bool Draw(WheelCollider wheel, IViewProjectionProvider camera,
        SysVec2 viewMin, SysVec2 viewSize, ImDrawListPtr draw, bool viewHovered) {
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) {
            activeWheel = null;
            activeHandle = -1;
        }

        Matrix4 vp = camera.GetViewMatrix() * camera.GetProjectionMatrix();
        Transform t = wheel.transform;
        Vector3 up = t.Up;
        Vector3 mount = t.WorldPosition;

        // The wheel centre sits at the rest position in edit mode (the same place the mesh/gizmo draw it).
        float restDrop = wheel.SuspensionTravel * wheel.SuspensionRestFraction;
        Vector3 centre = mount - up * restDrop;

        var changed = false;

        // RADIUS handle: top of the wheel circle. Drag along +up grows the radius.
        Vector3 radiusPos = centre + up * wheel.Radius;
        if (HandleSquare(wheel, 0, radiusPos, up, vp, viewMin, viewSize, draw, viewHovered,
                out float radiusDelta))
            grabValue = wheel.Radius;
        if (ReferenceEquals(activeWheel, wheel) && activeHandle == 0 && radiusDelta != 0f) {
            wheel.Radius = MathHelper.Clamp(grabValue + radiusDelta, 0.05f, 2f);
            changed = true;
        }

        // TRAVEL handle: at full droop. Drag along −up (downward) lengthens the travel.
        Vector3 travelPos = mount - up * wheel.SuspensionTravel;
        if (HandleSquare(wheel, 1, travelPos, -up, vp, viewMin, viewSize, draw, viewHovered,
                out float travelDelta))
            grabValue = wheel.SuspensionTravel;
        if (ReferenceEquals(activeWheel, wheel) && activeHandle == 1 && travelDelta != 0f) {
            wheel.SuspensionTravel = MathHelper.Clamp(grabValue + travelDelta, 0.01f, 1f);
            changed = true;
        }

        return changed;
    }

    // Draws one square handle; starts a drag (undo push + grab snapshot) when clicked. Outputs the
    // world distance dragged along `worldDir` since grab. Returns true on the grab frame.
    static bool HandleSquare(WheelCollider wheel, int index, Vector3 worldPos, Vector3 worldDir,
        Matrix4 vp, SysVec2 viewMin, SysVec2 viewSize, ImDrawListPtr draw, bool viewHovered,
        out float worldDelta) {
        worldDelta = 0f;
        if (!GizmoMath.Project(worldPos, vp, viewMin, viewSize, out SysVec2 px))
            return false;

        SysVec2 mouse = ImGui.GetIO().MousePos;
        bool active = ReferenceEquals(activeWheel, wheel) && activeHandle == index;
        bool hovered = viewHovered && activeWheel is null &&
                       MathF.Abs(mouse.X - px.X) <= 7f && MathF.Abs(mouse.Y - px.Y) <= 7f;

        uint fill = ImGui.GetColorU32(active
            ? new SysVec4(1f, 0.85f, 0.2f, 1f)
            : hovered
                ? new SysVec4(0.7f, 0.95f, 1f, 1f)
                : new SysVec4(0.4f, 0.85f, 1f, 0.9f));
        float half = active || hovered ? 6f : 4.5f;
        draw.AddRectFilled(px - new SysVec2(half, half), px + new SysVec2(half, half), fill, 2f);
        draw.AddRect(px - new SysVec2(half, half), px + new SysVec2(half, half),
            ImGui.GetColorU32(new SysVec4(0.1f, 0.1f, 0.1f, 1f)), 2f);

        var grabbed = false;
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) {
            activeWheel = wheel;
            activeHandle = index;
            grabAnchor = worldPos;
            grabDir = worldDir;
            GizmoMath.MouseRay(vp, viewMin, viewSize, mouse, out Vector3 rayO, out Vector3 rayD);
            grabParam = ClosestParamOnAxis(worldPos, worldDir, rayO, rayD);
            // Drag-start snapshot of this ONE wheel's entity -> scoped through EditorCommands.EditEntity
            // (PushEntity: selection survives, no whole-scene re-bake). The drag mutates wheel.Radius/
            // SuspensionTravel on later frames, so the grab-frame snapshot is preserved with a no-op
            // mutate -- byte-identical beyond the Push->PushEntity scoping.
            EditorCommands.EditEntity(wheel.Entity, "Edit Wheel", () => { });
            grabbed = true;
        }

        if (ReferenceEquals(activeWheel, wheel) && activeHandle == index &&
            ImGui.IsMouseDown(ImGuiMouseButton.Left)) {
            GizmoMath.MouseRay(vp, viewMin, viewSize, mouse, out Vector3 rayO, out Vector3 rayD);
            worldDelta = ClosestParamOnAxis(grabAnchor, grabDir, rayO, rayD) - grabParam;
        }
        return grabbed;
    }

    // Parameter t along the axis (origin + axis*t) closest to the mouse ray.
    static float ClosestParamOnAxis(Vector3 axisOrigin, Vector3 axisDir, Vector3 rayO, Vector3 rayD) {
        Vector3 w0 = axisOrigin - rayO;
        float a = Vector3.Dot(axisDir, axisDir);
        float b = Vector3.Dot(axisDir, rayD);
        float c = Vector3.Dot(rayD, rayD);
        float d = Vector3.Dot(axisDir, w0);
        float e = Vector3.Dot(rayD, w0);
        var denom = a * c - b * b;
        if (MathF.Abs(denom) < 1e-6f)
            return 0f;
        return (b * e - c * d) / denom;
    }
}
