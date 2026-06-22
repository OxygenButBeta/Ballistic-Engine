
namespace BallisticEngine;

public static class RibbonBuilder {
    public static int Build(IReadOnlyList<Vector3> points, int count, Vector3 cameraPos,
        float startWidth, float endWidth, Vector4 startColor, Vector4 endColor,
        ref RibbonVertex[] scratch) {
        int vcount = count * 2;
        if (scratch is null || scratch.Length < vcount)
            scratch = new RibbonVertex[Math.Max(vcount, 8)];
        if (count < 2)
            return 0;

        for (var i = 0; i < count; i++) {
            Vector3 pos = points[i];

            Vector3 dir;
            if (i == 0) dir = points[0] - points[1];
            else if (i == count - 1) dir = points[count - 2] - points[count - 1];
            else dir = points[i - 1] - points[i + 1];
            if (dir.LengthSquared() < 1e-10f) dir = Vector3.UnitX;
            dir = dir.Normalized();

            Vector3 toCam = cameraPos - pos;
            Vector3 side = Vector3.Cross(dir, toCam);
            side = side.LengthSquared() > 1e-10f ? side.Normalized() : Vector3.UnitY;

            float t = count > 1 ? i / (float)(count - 1) : 0f;
            float halfWidth = MathHelper.Lerp(startWidth, endWidth, t) * 0.5f;
            Vector4 color = Vector4.Lerp(startColor, endColor, t);

            scratch[i * 2 + 0] = new RibbonVertex {
                Position = pos + side * halfWidth, Uv = new Vector2(t, 0f), Color = color,
            };
            scratch[i * 2 + 1] = new RibbonVertex {
                Position = pos - side * halfWidth, Uv = new Vector2(t, 1f), Color = color,
            };
        }
        return vcount;
    }
}
