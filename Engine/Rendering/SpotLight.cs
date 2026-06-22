
namespace BallisticEngine;

public class SpotLight : Behaviour {
    [Header("Light")]
    [ColorUsage(hdr: true)]
    public Vector3 Color { get; set; } = Vector3.One;

    [Range(0f, 20000f)]
    public float Lumens { get; set; } = 3000f;

    [Range(0f, 100f)]
    public float Intensity { get; set; } = 1f;

    [Range(1500f, 12000f)]
    public float ColorTemperature { get; set; } = 4000f;

    public Vector3 PhysicalColor {
        get {
            float outer = System.MathF.Cos(MathHelper.DegreesToRadians(
                System.Math.Clamp(System.MathF.Max(OuterAngle, InnerAngle), 0f, 89.9f)));
            float solidAngle = System.MathF.Max(2f * System.MathF.PI * (1f - outer), 1e-3f);
            return Color * PhysicalLight.KelvinToRGB(ColorTemperature)
                         * (Lumens / solidAngle
                            * PhysicalLight.LuxToRadiance * PhysicalLight.PunctualIntensityScale * Intensity);
        }
    }

    [Tooltip("Distance the cone reaches, in world units.")]
    [Range(0f, 100f)]
    public float Range { get; set; } = 15f;

    [Tooltip("Physical radius of the emitter (world units). >0 gives a soft AREA-light specular " +
             "highlight with real angular size (Karis representative point). 0 = delta point (default).")]
    [Range(0f, 5f)]
    public float SourceRadius { get; set; }

    [Header("Cone")]
    [Range(0f, 90f)]
    public float InnerAngle { get; set; } = 25f;

    [Range(0f, 90f)]
    public float OuterAngle { get; set; } = 35f;

    [FoldoutGroup("Shadows", defaultOpen: false)]
    public bool CastShadows { get; set; } = true;

    [FoldoutGroup("Shadows", defaultOpen: false)]
    public float ShadowBias { get; set; } = 0.002f;

    protected internal override void OnAttach() {
        if (!RuntimeSet<SpotLight>.Contains(this))
            RuntimeSet<SpotLight>.Add(this);
    }

    protected internal override void OnDetach() {
        RuntimeSet<SpotLight>.Remove(this);
    }

    public override void OnDrawGizmos(IGizmos gizmos) {
        gizmos.Color = new Vector3(1f, 0.85f, 0.3f);
        gizmos.DrawIcon(transform.Position, GizmoIcon.Light);
    }

    public override void OnDrawGizmosSelected(IGizmos gizmos) {
        gizmos.Color = new Vector3(1f, 0.85f, 0.3f);
        gizmos.DrawWireCone(transform.Position, transform.Forward * Range, OuterAngle);
    }
}
