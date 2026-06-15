using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

// Scene-view resize handles for the selected IrradianceVolume: one draggable square per box
// face. Dragging moves that face along its axis (the opposite face stays put), updating
// Center + Size like Unity's box collider editing. Undo snapshots on grab.
internal static class VolumeBoundsHandles {
    static int activeFace = -1;
    static float grabParam;
    static Vector3 grabCenter, grabSize;

    public static bool IsInteracting => activeFace != -1;

    static readonly Vector3[] FaceAxes = {
        Vector3.UnitX, -Vector3.UnitX,
        Vector3.UnitY, -Vector3.UnitY,
        Vector3.UnitZ, -Vector3.UnitZ,
    };

    // Returns true when the volume changed this frame (caller marks the scene dirty).
    public static bool Draw(IrradianceVolume volume, IViewProjectionProvider camera,
        SysVec2 viewMin, SysVec2 viewSize, ImDrawListPtr draw, bool viewHovered) {
        Matrix4 vp = camera.GetViewMatrix() * camera.GetProjectionMatrix();
        SysVec2 mouse = ImGui.GetIO().MousePos;
        var changed = false;

        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            activeFace = -1;

        for (var f = 0; f < 6; f++) {
            Vector3 axis = FaceAxes[f];
            var axisIndex = f / 2;
            var halfExtent = volume.Size[axisIndex] * 0.5f;
            Vector3 facePos = volume.Center + axis * halfExtent;

            if (!GizmoMath.Project(facePos, vp, viewMin, viewSize, out SysVec2 px))
                continue;

            var hovered = viewHovered &&
                          MathF.Abs(mouse.X - px.X) <= 7f && MathF.Abs(mouse.Y - px.Y) <= 7f;
            var active = activeFace == f;

            uint fill = ImGui.GetColorU32(active
                ? new SysVec4(1f, 0.85f, 0.2f, 1f)
                : hovered
                    ? new SysVec4(1f, 1f, 0.6f, 1f)
                    : new SysVec4(0.75f, 1f, 0.6f, 0.9f));
            var half = (active || hovered ? 6f : 5f);
            draw.AddRectFilled(px - new SysVec2(half, half), px + new SysVec2(half, half), fill, 2f);
            draw.AddRect(px - new SysVec2(half, half), px + new SysVec2(half, half),
                ImGui.GetColorU32(new SysVec4(0.1f, 0.1f, 0.1f, 1f)), 2f);

            if (hovered && activeFace == -1 && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) {
                activeFace = f;
                grabCenter = volume.Center;
                grabSize = volume.Size;
                GizmoMath.MouseRay(vp, viewMin, viewSize, mouse, out Vector3 ro, out Vector3 rd);
                grabParam = ClosestParamOnAxis(facePos, axis, ro, rd);
                EditorUndo.Push("Resize Volume Bounds");
            }

            if (!active || !ImGui.IsMouseDown(ImGuiMouseButton.Left))
                continue;

            // Drag: how far along the face's outward axis the mouse ray has moved since grab.
            Vector3 grabFacePos = grabCenter + axis * (grabSize[axisIndex] * 0.5f);
            GizmoMath.MouseRay(vp, viewMin, viewSize, mouse, out Vector3 rayO, out Vector3 rayD);
            var t = ClosestParamOnAxis(grabFacePos, axis, rayO, rayD);
            var delta = t - grabParam;
            if (MathF.Abs(delta) < 1e-5f)
                continue;

            var newExtent = MathF.Max(grabSize[axisIndex] + delta, 0.5f);
            delta = newExtent - grabSize[axisIndex]; // re-derive after the clamp

            Vector3 size = volume.Size;
            Vector3 center = volume.Center;
            size[axisIndex] = newExtent;
            center[axisIndex] = grabCenter[axisIndex] + axis[axisIndex] * delta * 0.5f;
            volume.Size = size;
            volume.Center = center;
            changed = true;
        }

        return changed;
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
