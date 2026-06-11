using OpenTK.Mathematics;
using SysVec2 = System.Numerics.Vector2;

namespace BallisticEngine.Editor;

// Shared world<->screen projection + ray math for everything the editor draws over the Scene view
// (the transform gizmo, component gizmos, the world grid, the orientation cube). The Y-flip and the
// row-vector view*projection convention live HERE, in one place, so the handles, gizmos, grid and
// cube can never disagree about which way is up or which axis is which.
internal static class GizmoMath {
    // World point -> pixel inside the viewport rect. Returns false when behind the camera.
    public static bool Project(Vector3 world, Matrix4 vp, SysVec2 viewMin, SysVec2 viewSize, out SysVec2 pixel) {
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

    // Pixel -> world ray (origin on the near plane, normalized direction).
    public static void MouseRay(Matrix4 vp, SysVec2 viewMin, SysVec2 viewSize,
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

    // World units per screen pixel at the given distance (45° vertical fov), for constant-size handles.
    public static float WorldSizePerPixel(float distance, float viewHeightPx) {
        var worldHeight = 2f * distance * MathF.Tan(MathHelper.DegreesToRadians(45f) * 0.5f);
        return worldHeight / Math.Max(1f, viewHeightPx);
    }

    // A point on the circle of `radius` around `center` in the plane perpendicular to `axis`.
    public static Vector3 CirclePoint(Vector3 center, Vector3 axis, float radius, float angle) {
        Vector3 u = Vector3.Cross(axis, Math.Abs(axis.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY).Normalized();
        Vector3 v = Vector3.Cross(axis, u);
        return center + (u * MathF.Cos(angle) + v * MathF.Sin(angle)) * radius;
    }

    public static float DistanceToSegment(SysVec2 p, SysVec2 a, SysVec2 b) {
        SysVec2 ab = b - a;
        float lengthSq = ab.LengthSquared();
        if (lengthSq < 1e-5f)
            return SysVec2.Distance(p, a);
        float t = Math.Clamp(SysVec2.Dot(p - a, ab) / lengthSq, 0f, 1f);
        return SysVec2.Distance(p, a + ab * t);
    }
}
