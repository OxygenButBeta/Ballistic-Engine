
namespace BallisticEngine;

// One ribbon vertex the GL ribbon pass streams: world position, uv (U = start->end along the strip,
// V = 0/1 edge), RGBA. Shared by TrailRenderer and LineRenderer (both draw a camera-facing strip).
public struct RibbonVertex {
    public Vector3 Position;
    public Vector2 Uv;
    public Vector4 Color;
}

// How a ribbon composites. (Mirrors ParticleBlendMode so the GL pass picks the GL blend func.)
public enum RibbonBlendMode {
    Additive,   // lasers, energy, light streaks
    Alpha,      // smoke wakes, ropes, cables
}

// A source of ribbon vertices the GL pass renders — any component that wants a camera-facing strip
// (TrailRenderer's aging history, LineRenderer's explicit point list). The pass iterates one
// RuntimeSet<IRibbonSource> and draws each as a triangle strip.
public interface IRibbonSource {
    bool IsActive { get; }
    bool RibbonRenderable { get; }
    RibbonBlendMode BlendMode { get; }
    Texture2D RibbonTexture { get; }

    // Fills `vertices` (the source's reused scratch) with a camera-facing triangle strip and returns
    // the vertex count (0 if nothing to draw).
    int BuildRibbon(Vector3 cameraPos, out RibbonVertex[] vertices);
}

// Builds a camera-facing ribbon (triangle strip, 2 verts per point) from a world-space point list.
// Each point's side offset is perpendicular to BOTH the local segment direction AND the view
// direction, so the strip always faces the camera; width and color/alpha lerp start(0)->end(1) by
// normalized index. Shared by Trail/Line so the strip math lives in one place.
public static class RibbonBuilder {
    // points: world positions, FIRST = start (head). count = how many of them are valid.
    // Writes into `scratch` (grown as needed) and returns the vertex count (count*2, or 0 if < 2).
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

            // Segment direction at this point (toward the neighbour), for the perpendicular.
            Vector3 dir;
            if (i == 0) dir = points[0] - points[1];
            else if (i == count - 1) dir = points[count - 2] - points[count - 1];
            else dir = points[i - 1] - points[i + 1];
            if (dir.LengthSquared() < 1e-10f) dir = Vector3.UnitX;
            dir = dir.Normalized();

            Vector3 toCam = cameraPos - pos;
            Vector3 side = Vector3.Cross(dir, toCam);
            side = side.LengthSquared() > 1e-10f ? side.Normalized() : Vector3.UnitY;

            float t = count > 1 ? i / (float)(count - 1) : 0f;   // 0 = start, 1 = end
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
