
namespace BallisticEngine;

public class PointLight : Behaviour {
    [Header("Light")]
    [ColorUsage(hdr: true)]
    public Vector3 Color { get; set; } = Vector3.One;

    [Range(0f, 20000f)]
    public float Lumens { get; set; } = 1500f;

    [Range(0f, 100f)]
    public float Intensity { get; set; } = 1f;

    [Range(1500f, 12000f)]
    public float ColorTemperature { get; set; } = 2700f;

    public Vector3 PhysicalColor =>
        Color * PhysicalLight.KelvinToRGB(ColorTemperature)
              * (Lumens / (4f * System.MathF.PI)
                 * PhysicalLight.LuxToRadiance * PhysicalLight.PunctualIntensityScale * Intensity);

    [Tooltip("Distance the light reaches, in world units.")]
    [Range(0f, 100f)]
    public float Range { get; set; } = 10f;

    [Tooltip("Physical radius of the emitter sphere (world units). >0 gives a soft AREA-light " +
             "specular highlight with real angular size instead of a pinpoint (Karis representative " +
             "point). 0 = a classic delta point light (default, unchanged).")]
    [Range(0f, 5f)]
    public float SourceRadius { get; set; }

    [FoldoutGroup("Shadows", defaultOpen: false)]
    public bool CastShadows { get; set; } = true;

    [FoldoutGroup("Shadows", defaultOpen: false)]
    public float ShadowBias { get; set; } = 0.002f;

    protected internal override void OnAttach() {
        if (!RuntimeSet<PointLight>.Contains(this))
            RuntimeSet<PointLight>.Add(this);
    }

    protected internal override void OnDetach() {
        RuntimeSet<PointLight>.Remove(this);
    }

    public override void OnDrawGizmos(IGizmos gizmos) {
        gizmos.Color = new Vector3(1f, 0.85f, 0.3f);
        gizmos.DrawIcon(transform.Position, GizmoIcon.Light);
    }

    public override void OnDrawGizmosSelected(IGizmos gizmos) {
        gizmos.Color = new Vector3(1f, 0.85f, 0.3f);
        gizmos.DrawWireSphere(transform.Position, Range);
    }
}
