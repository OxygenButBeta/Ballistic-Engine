using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;
using SysVec4 = System.Numerics.Vector4;

namespace BallisticEngine.Editor;

internal static class ColliderHandles {
    static Collider activeCollider;
    static int activeHandle = -1;
    static Vector3 grabAnchor, grabDir;
    static float grabParam;
    static Vector3 grabSize, grabCenter;
    static float grabScalar;

    public static bool IsInteracting => activeCollider is not null;

    static readonly Vector3[] Axes = {
        Vector3.UnitX, -Vector3.UnitX,
        Vector3.UnitY, -Vector3.UnitY,
        Vector3.UnitZ, -Vector3.UnitZ,
    };

    public static bool Draw(Collider collider, IViewProjectionProvider camera,
        SysVec2 viewMin, SysVec2 viewSize, ImDrawListPtr draw, bool viewHovered) {
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) {
            activeCollider = null;
            activeHandle = -1;
        }

        Matrix4 vp = camera.GetViewMatrix() * camera.GetProjectionMatrix();
        Transform transform = collider.transform;
        Vector3 scale = transform.WorldMatrix.ExtractScale();
        Quaternion rotation = transform.WorldRotation;
        Vector3 shapeCenter = transform.WorldPosition + Vector3.Transform(collider.Center * scale, rotation);

        return collider switch {
            BoxCollider box => DrawBox(box, shapeCenter, rotation, scale, vp, viewMin, viewSize, draw, viewHovered),
            SphereCollider sphere => DrawSphere(sphere, shapeCenter, rotation, scale, vp, viewMin, viewSize, draw, viewHovered),
            CapsuleCollider capsule => DrawCapsule(capsule, shapeCenter, rotation, scale, vp, viewMin, viewSize, draw, viewHovered),
            _ => false,
        };
    }

    static bool DrawBox(BoxCollider box, Vector3 shapeCenter, Quaternion rotation, Vector3 scale,
        Matrix4 vp, SysVec2 viewMin, SysVec2 viewSize, ImDrawListPtr draw, bool viewHovered) {
        var changed = false;
        for (var f = 0; f < 6; f++) {
            Vector3 localAxis = Axes[f];
            var axisIndex = f / 2;
            float axisScale = MathF.Max(MathF.Abs(scale[axisIndex]), 1e-5f);
            Vector3 worldDir = Vector3.Transform(localAxis, rotation);
            Vector3 facePos = shapeCenter + worldDir * (box.Size[axisIndex] * 0.5f * axisScale);

            if (HandleSquare(box, f, facePos, worldDir, vp, viewMin, viewSize, draw, viewHovered, out float worldDelta)) {
                grabSize = box.Size;
                grabCenter = box.Center;
            }
            if (!ReferenceEquals(activeCollider, box) || activeHandle != f || worldDelta == 0f)
                continue;

            float localDelta = worldDelta / axisScale;
            float newExtent = MathF.Max(grabSize[axisIndex] + localDelta, 0.01f);
            localDelta = newExtent - grabSize[axisIndex];

            Vector3 size = box.Size;
            Vector3 center = box.Center;
            size[axisIndex] = newExtent;
            center[axisIndex] = grabCenter[axisIndex] + localAxis[axisIndex] * localDelta * 0.5f;
            box.Size = size;
            box.Center = center;
            changed = true;
        }
        return changed;
    }

    static bool DrawSphere(SphereCollider sphere, Vector3 shapeCenter, Quaternion rotation, Vector3 scale,
        Matrix4 vp, SysVec2 viewMin, SysVec2 viewSize, ImDrawListPtr draw, bool viewHovered) {
        float radiusScale = MathF.Max(
            MathF.Max(MathF.Abs(scale.X), MathF.Max(MathF.Abs(scale.Y), MathF.Abs(scale.Z))), 1e-5f);

        var changed = false;
        for (var f = 0; f < 6; f++) {
            Vector3 worldDir = Vector3.Transform(Axes[f], rotation);
            Vector3 handlePos = shapeCenter + worldDir * (sphere.Radius * radiusScale);

            if (HandleSquare(sphere, f, handlePos, worldDir, vp, viewMin, viewSize, draw, viewHovered, out float worldDelta))
                grabScalar = sphere.Radius;
            if (!ReferenceEquals(activeCollider, sphere) || activeHandle != f || worldDelta == 0f)
                continue;

            sphere.Radius = MathF.Max(grabScalar + worldDelta / radiusScale, 0.01f);
            changed = true;
        }
        return changed;
    }

    static bool DrawCapsule(CapsuleCollider capsule, Vector3 shapeCenter, Quaternion rotation, Vector3 scale,
        Matrix4 vp, SysVec2 viewMin, SysVec2 viewSize, ImDrawListPtr draw, bool viewHovered) {
        float radiusScale = MathF.Max(MathF.Max(MathF.Abs(scale.X), MathF.Abs(scale.Z)), 1e-5f);
        float heightScale = MathF.Max(MathF.Abs(scale.Y), 1e-5f);

        var changed = false;
        for (var f = 0; f < 6; f++) {
            Vector3 worldDir = Vector3.Transform(Axes[f], rotation);
            bool isHeightHandle = f is 2 or 3;
            Vector3 handlePos = shapeCenter + worldDir * (isHeightHandle
                ? capsule.Height * 0.5f * heightScale
                : capsule.Radius * radiusScale);

            if (HandleSquare(capsule, f, handlePos, worldDir, vp, viewMin, viewSize, draw, viewHovered, out float worldDelta))
                grabScalar = isHeightHandle ? capsule.Height : capsule.Radius;
            if (!ReferenceEquals(activeCollider, capsule) || activeHandle != f || worldDelta == 0f)
                continue;

            if (isHeightHandle)
                capsule.Height = MathF.Max(grabScalar + 2f * (worldDelta / heightScale), 0.01f);
            else
                capsule.Radius = MathF.Max(grabScalar + worldDelta / radiusScale, 0.01f);
            changed = true;
        }
        return changed;
    }

    static bool HandleSquare(Collider collider, int index, Vector3 worldPos, Vector3 worldDir,
        Matrix4 vp, SysVec2 viewMin, SysVec2 viewSize, ImDrawListPtr draw, bool viewHovered,
        out float worldDelta) {
        worldDelta = 0f;
        if (!GizmoMath.Project(worldPos, vp, viewMin, viewSize, out SysVec2 px))
            return false;

        SysVec2 mouse = ImGui.GetIO().MousePos;
        bool active = ReferenceEquals(activeCollider, collider) && activeHandle == index;
        bool hovered = viewHovered && activeCollider is null &&
                       MathF.Abs(mouse.X - px.X) <= 7f && MathF.Abs(mouse.Y - px.Y) <= 7f;

        uint fill = ImGui.GetColorU32(active
            ? new SysVec4(1f, 0.85f, 0.2f, 1f)
            : hovered
                ? new SysVec4(1f, 1f, 0.6f, 1f)
                : new SysVec4(0.55f, 1f, 0.55f, 0.9f));
        float half = active || hovered ? 6f : 4.5f;
        draw.AddRectFilled(px - new SysVec2(half, half), px + new SysVec2(half, half), fill, 2f);
        draw.AddRect(px - new SysVec2(half, half), px + new SysVec2(half, half),
            ImGui.GetColorU32(new SysVec4(0.1f, 0.1f, 0.1f, 1f)), 2f);

        var grabbed = false;
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) {
            activeCollider = collider;
            activeHandle = index;
            grabAnchor = worldPos;
            grabDir = worldDir;
            GizmoMath.MouseRay(vp, viewMin, viewSize, mouse, out Vector3 rayO, out Vector3 rayD);
            grabParam = ClosestParamOnAxis(worldPos, worldDir, rayO, rayD);
            EditorCommands.EditEntity(collider.Entity, "Resize Collider", () => { });
            grabbed = true;
        }

        if (ReferenceEquals(activeCollider, collider) && activeHandle == index &&
            ImGui.IsMouseDown(ImGuiMouseButton.Left)) {
            GizmoMath.MouseRay(vp, viewMin, viewSize, mouse, out Vector3 rayO, out Vector3 rayD);
            worldDelta = ClosestParamOnAxis(grabAnchor, grabDir, rayO, rayD) - grabParam;
        }
        return grabbed;
    }

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
