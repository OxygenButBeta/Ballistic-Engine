using OpenTK.Mathematics;

namespace BallisticEngine;

// One sample along a trail: a world position and the age (seconds) since it was laid down. The GL
// ribbon pass reads these to build a camera-facing strip that narrows + fades toward the tail.
public struct TrailPoint {
    public Vector3 Position;
    public float Age;
}

// A ribbon trail that follows the entity (Unity's TrailRenderer) — bullet tracers, sword swings,
// rocket/vehicle wakes, light streaks. Each frame it samples the transform position; once it has
// moved past MinVertexDistance a new point is laid down, points age out after Time seconds, and the
// component exposes the point history for the GL pass to ribbonize. CPU point management lives here
// (Engine layer); the camera-facing ribbon mesh is built in OpenGL/GLTrailPass.
//
// Driven from the renderer (TrailRenderer.AdvanceAll), like ParticleSystem — so it also previews in
// the editor and steps exactly once per frame.
[Component("Trail Renderer", "Effects")]
public class TrailRenderer : Behaviour {
    [Tooltip("Seconds a trail point survives before it fades out of the tail.")]
    [Range(0.05f, 30f)]
    public float Time { get; set; } = 1f;

    [Tooltip("Minimum world distance the emitter must move before a new point is laid down. Smaller = " +
             "smoother curves, more points.")]
    [Range(0.001f, 10f)]
    public float MinVertexDistance { get; set; } = 0.1f;

    [Tooltip("Ribbon width at the head (newest point).")]
    [Range(0f, 50f)]
    public float StartWidth { get; set; } = 0.3f;

    [Tooltip("Ribbon width at the tail (oldest point). 0 = taper to a point.")]
    [Range(0f, 50f)]
    public float EndWidth { get; set; }

    [Tooltip("RGB color at the head.")]
    public Vector3 StartColor { get; set; } = new(1f, 1f, 1f);

    [Tooltip("Alpha at the head.")]
    [Range(0f, 1f)]
    public float StartAlpha { get; set; } = 1f;

    [Tooltip("RGB color at the tail.")]
    public Vector3 EndColor { get; set; } = new(1f, 1f, 1f);

    [Tooltip("Alpha at the tail — fade out by setting this to 0.")]
    [Range(0f, 1f)]
    public float EndAlpha { get; set; }

    [Tooltip("How the ribbon composites. Additive = energy/light streaks; Alpha = smoke/dust wakes.")]
    public ParticleBlendMode BlendMode { get; set; } = ParticleBlendMode.Additive;

    [Tooltip("Optional ribbon texture, stretched head->tail along the strip. Unassigned = flat color.")]
    public Texture2D Texture { get; set; }

    [Tooltip("Stop laying new points (the existing tail still ages out). For one-shot streaks.")]
    public bool Emitting { get; set; } = true;

    // ---- Point history (runtime-only) ---------------------------------------

    // Newest point is at index 0 (head); the oldest is at the end (tail). A small ring would be
    // faster, but a list is simple and trails are short; revisit if profiling demands.
    readonly List<TrailPoint> points = new(64);
    bool hasLastSample;
    Vector3 lastSample;

    [NotSerialized]
    public int PointCount => points.Count;

    protected internal override void OnAttach() {
        if (!RuntimeSet<TrailRenderer>.Contains(this))
            RuntimeSet<TrailRenderer>.Add(this);
    }

    protected internal override void OnDetach() {
        RuntimeSet<TrailRenderer>.Remove(this);
    }

    // A trail is renderable once it has at least a segment (2 points).
    public bool IsRenderable => points.Count >= 2;

    // ---- Per-frame advance --------------------------------------------------

    public static void AdvanceAll(float dt) {
        dt = MathHelper.Clamp(dt, 0f, 0.1f);
        if (dt <= 0f)
            return;
        foreach (TrailRenderer trail in RuntimeSet<TrailRenderer>.ReadOnlyCollection)
            if (trail.IsActive)
                trail.Advance(dt);
    }

    void Advance(float dt) {
        // Age every point; drop the ones past their lifetime (from the tail).
        for (var i = 0; i < points.Count; i++) {
            TrailPoint p = points[i];
            p.Age += dt;
            points[i] = p;
        }
        while (points.Count > 0 && points[^1].Age >= Time)
            points.RemoveAt(points.Count - 1);

        if (!Emitting)
            return;

        Vector3 pos = transform.WorldPosition;
        if (!hasLastSample) {
            points.Insert(0, new TrailPoint { Position = pos, Age = 0f });
            lastSample = pos;
            hasLastSample = true;
            return;
        }

        // Lay a new head point only once the emitter has moved far enough (keeps the ribbon smooth
        // and bounded). The newest point always tracks the current position so the head stays attached.
        if (points.Count > 0) {
            TrailPoint head = points[0];
            head.Position = pos;
            points[0] = head;
        }
        if ((pos - lastSample).LengthSquared >= MinVertexDistance * MinVertexDistance) {
            points.Insert(0, new TrailPoint { Position = pos, Age = 0f });
            lastSample = pos;
        }
    }

    // Clears the trail (e.g. on teleport, so it doesn't streak across the jump).
    public void Clear() {
        points.Clear();
        hasLastSample = false;
    }

    // ---- Render snapshot ----------------------------------------------------

    // Exposes the point history for the GL ribbon pass (newest first). Width/color are evaluated by
    // the pass from the normalized tail position; this just hands over positions + ages.
    public IReadOnlyList<TrailPoint> Points => points;

    // One ribbon vertex the GL pass streams: world position, uv (U = head->tail, V = 0/1 edge), RGBA.
    public struct RibbonVertex {
        public Vector3 Position;
        public Vector2 Uv;
        public Vector4 Color;
    }

    RibbonVertex[] ribbonScratch;

    // Builds a camera-facing ribbon from the point history into `vertices` as a TRIANGLE STRIP (two
    // verts per point: left + right edge). Returns the vertex count (0 if < 2 points). Each point's
    // side offset is perpendicular to BOTH the local segment direction and the view direction, so the
    // strip always faces the camera; width and color lerp head(0)->tail(1) by normalized index.
    public int BuildRibbon(Vector3 cameraPos, out RibbonVertex[] vertices) {
        int n = points.Count;
        int vcount = n * 2;
        if (ribbonScratch is null || ribbonScratch.Length < vcount)
            ribbonScratch = new RibbonVertex[Math.Max(vcount, 8)];

        if (n < 2) {
            vertices = ribbonScratch;
            return 0;
        }

        for (var i = 0; i < n; i++) {
            Vector3 pos = points[i].Position;

            // Segment direction at this point (toward the neighbour), for the perpendicular.
            Vector3 dir;
            if (i == 0) dir = points[0].Position - points[1].Position;
            else if (i == n - 1) dir = points[n - 2].Position - points[n - 1].Position;
            else dir = points[i - 1].Position - points[i + 1].Position;
            if (dir.LengthSquared < 1e-10f) dir = Vector3.UnitX;
            dir = dir.Normalized();

            Vector3 toCam = cameraPos - pos;
            Vector3 side = Vector3.Cross(dir, toCam);
            side = side.LengthSquared > 1e-10f ? side.Normalized() : Vector3.UnitY;

            float t = n > 1 ? i / (float)(n - 1) : 0f;   // 0 = head, 1 = tail
            float halfWidth = MathHelper.Lerp(StartWidth, EndWidth, t) * 0.5f;
            Vector3 rgb = Vector3.Lerp(StartColor, EndColor, t);
            float alpha = MathHelper.Lerp(StartAlpha, EndAlpha, t);
            var color = new Vector4(rgb, alpha);

            ribbonScratch[i * 2 + 0] = new RibbonVertex {
                Position = pos + side * halfWidth, Uv = new Vector2(t, 0f), Color = color,
            };
            ribbonScratch[i * 2 + 1] = new RibbonVertex {
                Position = pos - side * halfWidth, Uv = new Vector2(t, 1f), Color = color,
            };
        }

        vertices = ribbonScratch;
        return vcount;
    }
}
