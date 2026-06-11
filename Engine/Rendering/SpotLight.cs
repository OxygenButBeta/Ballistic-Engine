using OpenTK.Mathematics;

namespace BallisticEngine;

// Spot light pointing along the entity's forward axis, forward-shaded. Registers on
// attach so the editor viewport is lit without entering play mode.
public class SpotLight : Behaviour {
    [Header("Light")]
    [ColorUsage(hdr: true)]
    public Vector3 Color { get; set; } = Vector3.One;

    // PHYSICAL: luminous power in lumens. A spot concentrates its flux into the cone, so candela
    // = lumens / (2pi*(1-cos(outer))) - the solid angle of the cone. Scaled into HDR radiance by
    // the shared lux factor so it balances with the sun, point lights and IBL under EV exposure.
    [Range(0f, 20000f)]
    public float Lumens { get; set; } = 3000f;

    // Artist multiplier on top of the physical lumens (see PointLight.Intensity). 1 = a believable
    // spot under the sun's exposure; PunctualIntensityScale handles the lumens-vs-lux unit balance.
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

    // Cone angles in degrees; full brightness inside Inner, fades to zero at Outer.
    [Header("Cone")]
    [Range(0f, 90f)]
    public float InnerAngle { get; set; } = 25f;

    [Range(0f, 90f)]
    public float OuterAngle { get; set; } = 35f;

    // Shadowed spots render the scene once into the punctual shadow array; the renderer
    // shadows the first few CastShadows lights (slots are limited).
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
