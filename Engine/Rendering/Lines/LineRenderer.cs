
namespace BallisticEngine;

[Component("Line Renderer", "Effects")]
public class LineRenderer : Behaviour, IRibbonSource {
    [Tooltip("Width at the first point.")]
    [Range(0f, 50f)]
    public float StartWidth { get; set; } = 0.1f;

    [Tooltip("Width at the last point. Equal to StartWidth = a constant-width line.")]
    [Range(0f, 50f)]
    public float EndWidth { get; set; } = 0.1f;

    [Tooltip("RGB color at the first point.")]
    public Vector3 StartColor { get; set; } = new(1f, 1f, 1f);

    [Range(0f, 1f)]
    public float StartAlpha { get; set; } = 1f;

    [Tooltip("RGB color at the last point.")]
    public Vector3 EndColor { get; set; } = new(1f, 1f, 1f);

    [Range(0f, 1f)]
    public float EndAlpha { get; set; } = 1f;

    [Tooltip("How the line composites. Additive = lasers/energy; Alpha = ropes/cables.")]
    public RibbonBlendMode BlendMode { get; set; } = RibbonBlendMode.Additive;

    [Tooltip("Optional texture, stretched start->end along the line.")]
    public Texture2D Texture { get; set; }

    [Tooltip("Treat points as local to the entity (the line follows/rotates with the transform). Off = world space.")]
    public bool UseLocalSpace { get; set; }

    [NotSerialized]
    public List<Vector3> Points { get; set; } = new();

    public int PointCount => Points.Count;

    public void SetPositions(IReadOnlyList<Vector3> positions) {
        Points.Clear();
        for (var i = 0; i < positions.Count; i++)
            Points.Add(positions[i]);
    }

    public void SetPositionCount(int count) {
        count = Math.Max(0, count);
        while (Points.Count < count) Points.Add(Vector3.Zero);
        while (Points.Count > count) Points.RemoveAt(Points.Count - 1);
    }

    public void SetPosition(int index, Vector3 position) {
        if (index < 0) return;
        while (Points.Count <= index) Points.Add(Vector3.Zero);
        Points[index] = position;
    }

    public void SetLine(Vector3 start, Vector3 end) {
        Points.Clear();
        Points.Add(start);
        Points.Add(end);
    }

    protected internal override void OnAttach() {
        if (!RuntimeSet<IRibbonSource>.Contains(this))
            RuntimeSet<IRibbonSource>.Add(this);
    }

    protected internal override void OnDetach() {
        RuntimeSet<IRibbonSource>.Remove(this);
    }

    bool IRibbonSource.RibbonRenderable => Points.Count >= 2;
    RibbonBlendMode IRibbonSource.BlendMode => BlendMode;
    Texture2D IRibbonSource.RibbonTexture => Texture;

    RibbonVertex[] ribbonScratch;
    readonly List<Vector3> worldScratch = new(16);

    public int BuildRibbon(Vector3 cameraPos, out RibbonVertex[] vertices) {
        worldScratch.Clear();
        if (UseLocalSpace) {
            Matrix4 world = transform.WorldMatrix;
            for (var i = 0; i < Points.Count; i++)
                worldScratch.Add(Vector3.Transform(Points[i], world));
        }
        else {
            for (var i = 0; i < Points.Count; i++)
                worldScratch.Add(Points[i]);
        }

        int count = RibbonBuilder.Build(worldScratch, worldScratch.Count, cameraPos,
            StartWidth, EndWidth,
            new Vector4(StartColor, StartAlpha), new Vector4(EndColor, EndAlpha),
            ref ribbonScratch);
        vertices = ribbonScratch;
        return count;
    }

    public override void OnDrawGizmosSelected(IGizmos gizmos) {
        if (Points.Count < 2)
            return;
        gizmos.Color = StartColor;
        Matrix4 world = UseLocalSpace ? transform.WorldMatrix : Matrix4.Identity;
        Vector3 prev = UseLocalSpace ? Vector3.Transform(Points[0], world) : Points[0];
        for (var i = 1; i < Points.Count; i++) {
            Vector3 cur = UseLocalSpace ? Vector3.Transform(Points[i], world) : Points[i];
            gizmos.DrawLine(prev, cur);
            prev = cur;
        }
    }
}
