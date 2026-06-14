using OpenTK.Mathematics;

namespace BallisticEngine;

// Local point light, forward-shaded. Registers on attach so the editor viewport is lit
// without entering play mode. The renderer uploads the first MaxPointLights active lights.
public class PointLight : Behaviour {
    [Header("Light")]
    [ColorUsage(hdr: true)]
    public Vector3 Color { get; set; } = Vector3.One;

    // PHYSICAL: luminous power in lumens (a 100W-equivalent bulb ~ 1500 lm, a candle ~ 12 lm).
    // Converted to candela (lm / 4pi for an isotropic point source) then to HDR radiance via
    // the shared lux scale, so it balances against the sun + IBL under the EV exposure. The
    // shader already applies inverse-square falloff, which is what makes lumens physical.
    [Range(0f, 20000f)]
    public float Lumens { get; set; } = 1500f;

    // Artist multiplier on top of the physical lumens — the dial you actually reach for. 1 = a
    // believable bulb under the same exposure as the sun (lumens stay real; PunctualIntensityScale
    // does the unit balancing). Push higher for a brighter source without leaving lumen-land.
    [Range(0f, 100f)]
    public float Intensity { get; set; } = 1f;

    [Range(1500f, 12000f)]
    public float ColorTemperature { get; set; } = 2700f; // warm tungsten by default

    // candela = lumens / 4pi; scaled into HDR (LuxToRadiance) and lifted into the sun's EV range
    // by PunctualIntensityScale * Intensity. Tint by Color * temperature.
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

    // Shadowed point lights render the scene into 6 cube faces of the punctual shadow
    // array; the renderer shadows the first few CastShadows lights (slots are limited).
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
