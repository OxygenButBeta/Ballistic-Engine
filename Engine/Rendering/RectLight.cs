
namespace BallisticEngine;

public class RectLight : Behaviour {
    [Header("Light")]
    [ColorUsage(hdr: true)]
    public Vector3 Color { get; set; } = Vector3.One;

    [Range(0f, 50000f)]
    public float Lumens { get; set; } = 4000f;

    [Range(0f, 100f)]
    public float Intensity { get; set; } = 1f;

    [Range(1500f, 12000f)]
    public float ColorTemperature { get; set; } = 6500f;

    [Tooltip("Rect full width in local X, world units.")]
    [Range(0.01f, 50f)]
    public float Width { get; set; } = 1f;

    [Tooltip("Rect full height in local Y, world units.")]
    [Range(0.01f, 50f)]
    public float Height { get; set; } = 1f;

    [Tooltip("Distance the panel reaches, in world units (influence cutoff used for culling).")]
    [Range(0f, 200f)]
    public float Range { get; set; } = 20f;

    [Tooltip("Emit from BOTH faces of the rect (a lamp panel visible from behind). Off = front face only " +
             "(the local +Z side). Two-sided halves the per-face radiance for the same total lumens.")]
    public bool TwoSided { get; set; }

    public Vector3 PhysicalColor {
        get {
            float area = System.MathF.Max(Width * Height, 1e-4f);
            float norm = area * System.MathF.PI * (TwoSided ? 2f : 1f);
            return Color * PhysicalLight.KelvinToRGB(ColorTemperature)
                         * (Lumens / norm
                            * PhysicalLight.LuxToRadiance * PhysicalLight.PunctualIntensityScale * Intensity);
        }
    }

    protected internal override void OnAttach() {
        if (!RuntimeSet<RectLight>.Contains(this))
            RuntimeSet<RectLight>.Add(this);
    }

    protected internal override void OnDetach() {
        RuntimeSet<RectLight>.Remove(this);
    }

    public override void OnDrawGizmos(IGizmos gizmos) {
        gizmos.Color = new Vector3(1f, 0.85f, 0.3f);
        gizmos.DrawIcon(transform.Position, GizmoIcon.Light);
    }

    public override void OnDrawGizmosSelected(IGizmos gizmos) {
        gizmos.Color = new Vector3(1f, 0.85f, 0.3f);
        Vector3 c = transform.Position;
        Vector3 r = transform.Right * (Width * 0.5f);
        Vector3 u = transform.Up * (Height * 0.5f);
        Vector3 p0 = c - r - u, p1 = c + r - u, p2 = c + r + u, p3 = c - r + u;
        gizmos.DrawLine(p0, p1);
        gizmos.DrawLine(p1, p2);
        gizmos.DrawLine(p2, p3);
        gizmos.DrawLine(p3, p0);
        gizmos.DrawLine(c, c + transform.Forward * System.MathF.Max(Width, Height) * 0.25f);
    }
}
