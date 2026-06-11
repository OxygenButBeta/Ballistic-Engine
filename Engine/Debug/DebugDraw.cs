using OpenTK.Mathematics;

namespace BallisticEngine;

// Engine-side buffer of debug line segments requested by game code (Debug.DrawLine/DrawRay) and
// engine systems. The engine layer is GL-free, so this just ACCUMULATES world-space colored
// segments; a host renderer drains them each frame and paints them (the editor reuses its gizmo
// line projection — see EditorApplication.DrawComponentGizmos). In a headless run nothing drains
// the buffer, so Expire keeps it from growing without bound.
//
// Segments may carry a duration: 0 = this frame only (cleared after the drain), >0 = persist for
// that many seconds (Unity's Debug.DrawLine duration). Persisted segments survive across frames so
// you can draw a hit marker in a callback and still see it.
public static class DebugDraw {
    public struct Segment {
        public Vector3 From;
        public Vector3 To;
        public Vector3 Color;     // linear RGB, 0..1 (matches IGizmos.Color)
        public float ExpiresAt;   // Time.TotalTime when it should drop; <=0 means single-frame
    }

    static readonly List<Segment> segments = new(capacity: 256);
    static readonly Vector3 DefaultColor = new(1f, 1f, 1f);

    // Whether ANY consumer wants debug lines. The editor sets this true; a shipped player leaves it
    // false so Draw* calls early-out to nothing (no buffer growth, no overhead in release play).
    public static bool Enabled { get; set; }

    public static IReadOnlyList<Segment> Segments => segments;

    public static void DrawLine(Vector3 from, Vector3 to, Vector3 color, float duration = 0f) {
        if (!Enabled)
            return;
        segments.Add(new Segment {
            From = from,
            To = to,
            Color = color,
            ExpiresAt = duration > 0f ? (float)Time.TotalTime + duration : 0f,
        });
    }

    public static void DrawLine(Vector3 from, Vector3 to) => DrawLine(from, to, DefaultColor);

    public static void DrawRay(Vector3 origin, Vector3 direction, Vector3 color, float duration = 0f) =>
        DrawLine(origin, origin + direction, color, duration);

    public static void DrawRay(Vector3 origin, Vector3 direction) =>
        DrawLine(origin, origin + direction, DefaultColor);

    // Axis-aligned (rotation-applied) wire box from center+size, as 12 segments.
    public static void DrawWireCube(Vector3 center, Vector3 size, Vector3 color,
        Quaternion rotation, float duration = 0f) {
        if (!Enabled)
            return;
        Vector3 h = size * 0.5f;
        Span<Vector3> c = stackalloc Vector3[8];
        int k = 0;
        for (int xi = -1; xi <= 1; xi += 2)
            for (int yi = -1; yi <= 1; yi += 2)
                for (int zi = -1; zi <= 1; zi += 2)
                    c[k++] = center + Vector3.Transform(new Vector3(h.X * xi, h.Y * yi, h.Z * zi), rotation);
        for (int i = 0; i < 8; i++)
            for (int j = i + 1; j < 8; j++)
                if (System.Numerics.BitOperations.PopCount((uint)(i ^ j)) == 1)
                    DrawLine(c[i], c[j], color, duration);
    }

    public static void DrawWireSphere(Vector3 center, float radius, Vector3 color, float duration = 0f) {
        if (!Enabled)
            return;
        const int seg = 24;
        for (int axis = 0; axis < 3; axis++) {
            Vector3 prev = default;
            for (int s = 0; s <= seg; s++) {
                float a = s / (float)seg * MathF.Tau;
                float ca = MathF.Cos(a) * radius, sa = MathF.Sin(a) * radius;
                Vector3 p = axis switch {
                    0 => new Vector3(0, ca, sa),
                    1 => new Vector3(ca, 0, sa),
                    _ => new Vector3(ca, sa, 0),
                } + center;
                if (s > 0)
                    DrawLine(prev, p, color, duration);
                prev = p;
            }
        }
    }

    // Called by the host AFTER draining (editor) or once per frame (headless): drop single-frame
    // segments and any timed segment that has expired. Persisted segments stay.
    public static void Expire() {
        float now = (float)Time.TotalTime;
        for (int i = segments.Count - 1; i >= 0; i--) {
            Segment s = segments[i];
            if (s.ExpiresAt <= 0f || s.ExpiresAt <= now)
                segments.RemoveAt(i);
        }
    }

    // Single-frame segments must clear even when no host drains them (headless/shipping): the host
    // calls this each frame. Equivalent to Expire here, kept as a named entry for clarity.
    public static void Clear() => segments.Clear();
}
