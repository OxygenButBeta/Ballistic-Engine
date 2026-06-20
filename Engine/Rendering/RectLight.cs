
namespace BallisticEngine;

// Area / RECT light (Unreal RectLight equivalent) — a flat rectangular emitter shaded with Linearly-
// Transformed Cosines (Heitz et al. 2016) in the DX12 deferred pass. Registers on attach so the editor
// viewport is lit without entering play mode. The rect lies in the entity's local XY plane, emitting along
// the entity's forward (+Z) axis; Width/Height are its full extents in local X/Y world units.
//
// NO SHADOWS in v1 — area-light shadows (ray-traced soft shadows / shadowed LTC) are a documented follow-up.
public class RectLight : Behaviour {
    [Header("Light")]
    [ColorUsage(hdr: true)]
    public Vector3 Color { get; set; } = Vector3.One;

    // PHYSICAL: luminous power in lumens emitted by the whole rect. A rect of area A emitting power Φ as a
    // diffuse (Lambertian) emitter has radiance L = Φ / (A * π) (the cosine-weighted hemisphere integral over
    // the front face gives the π; area gives the per-unit-area split). TwoSided halves it (power spreads over
    // both faces). Scaled into HDR radiance by the shared lux factor so it balances with the sun + point/spot
    // lights + IBL under EV exposure — exactly the PointLight/SpotLight normalization, only the solid-angle/area
    // term differs (point = /4π, spot = /cone, rect = /(area·π)).
    [Range(0f, 50000f)]
    public float Lumens { get; set; } = 4000f;

    // Artist multiplier on top of the physical lumens (see PointLight.Intensity). 1 = a believable panel under
    // the sun's exposure; PunctualIntensityScale handles the lumens-vs-lux unit balance.
    [Range(0f, 100f)]
    public float Intensity { get; set; } = 1f;

    [Range(1500f, 12000f)]
    public float ColorTemperature { get; set; } = 6500f; // neutral panel by default

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

    // radiance = luminous power / (area * π) for a one-sided Lambertian rect (two-sided → /2 again). Tinted by
    // Color * temperature, lifted into the sun's EV range by PunctualIntensityScale * Intensity. Mirrors the
    // PointLight/SpotLight pattern; only the area/π normalization is rect-specific.
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
        // The four corners of the rect quad.
        Vector3 p0 = c - r - u, p1 = c + r - u, p2 = c + r + u, p3 = c - r + u;
        gizmos.DrawLine(p0, p1);
        gizmos.DrawLine(p1, p2);
        gizmos.DrawLine(p2, p3);
        gizmos.DrawLine(p3, p0);
        // A short normal stub so the emitting face is visible.
        gizmos.DrawLine(c, c + transform.Forward * System.MathF.Max(Width, Height) * 0.25f);
    }
}
