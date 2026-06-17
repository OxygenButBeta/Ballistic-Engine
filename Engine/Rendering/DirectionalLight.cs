namespace BallisticEngine;

// CPU-side lighting values the renderer pushes as uniforms. Built from the active
// DirectionalLight, or from sensible defaults when a scene has no light (edit mode / empty scene).
public readonly struct LightUniforms {
    public readonly Vector3 Direction;   // toward the light (-forward)
    public readonly Vector3 Color;       // intensity * color
    public readonly float AmbientIntensity;

    public LightUniforms(Vector3 direction, Vector3 color, float ambientIntensity) {
        Direction = direction;
        Color = color;
        AmbientIntensity = ambientIntensity;
    }

    public static LightUniforms Resolve() {
        DirectionalLight light = DirectionalLight.Instance;
        if (light is null)
            // Default midday sun: ~5500K, a touch of illuminance in our lux scale.
            return new LightUniforms(Vector3.UnitY,
                PhysicalLight.KelvinToRGB(5500f) * (80000f * PhysicalLight.LuxToRadiance), 0.3f);

        return new LightUniforms(
            -light.transform.Forward,
            light.PhysicalColor,
            light.ambientIntensity);
    }
}

public class DirectionalLight : Behaviour
{
    // The registered scene sun. The renderer (LightUniforms.Resolve + every DirectionalLight.Instance?.X
    // read) treats null as "no sun". Unlike point/spot lights — which are gathered from a RuntimeSet that
    // is filtered by IsActive every frame — the sun is a single static, so the active gate lives HERE:
    // the getter hides a disabled component or a disabled (in-hierarchy) entity, so closing either stops
    // the sun without needing OnDetach. Set in OnAttach/OnBegin, cleared in OnDetach.
    static DirectionalLight registered;

    public static DirectionalLight Instance =>
        registered is { IsActive: true } ? registered : null;

    // Scene teardown (StopPlay / scene clear) drops the registration wholesale, mirroring the
    // RuntimeSet clears — the re-deserialized scene's light re-registers in OnAttach.
    public static void Clear() => registered = null;

    public Vector3 AmbientLight => _ambientColor * ambientIntensity;
    Vector3 _ambientColor = new(0.35f, 0.40f, 0.45f);

    [Header("Lighting")]
    [Range(0f, 2f)]
    public float ambientIntensity = .3f;

    // PHYSICAL sun. Illuminance in lux (clear midday sun on a surface ~ 80-120k lux; overcast
    // ~ 10-25k), and colour as a blackbody temperature in Kelvin (5500 = noon daylight, 6500 =
    // overcast/white, 4000 = warm afternoon). The renderer scales lux into HDR radiance via
    // PhysicalLight.LuxToRadiance so it balances against the IBL and EV exposure - no unitless
    // "intensity" multiplier to guess at.
    [Range(0f, 150000f)]
    public float Illuminance = 80000f;     // lux

    [Range(1500f, 12000f)]
    public float ColorTemperature = 5500f; // Kelvin

    // Final linear-RGB radiance pushed to the shader: temperature hue * lux (scaled to HDR).
    public Vector3 PhysicalColor =>
        PhysicalLight.KelvinToRGB(ColorTemperature) * (Illuminance * PhysicalLight.LuxToRadiance);

    // Back-compat shim: older code/serialised scenes referencing LightColor still resolve.
    public Vector3 LightColor => PhysicalColor;

    // Apparent size of the sun disk in the sky, in degrees of arc (the real sun is ~0.53).
    // Drives the width of specular highlights and the softness sun shadows will gain with
    // distance; bigger = an overcast/hazier sun.
    [Range(0.1f, 10f)]
    public float AngularDiameter = 0.53f;

    // How far from the camera the directional shadow map reaches (world units).
    [FoldoutGroup("Shadows", defaultOpen: false)]
    [Tooltip("How far from the camera the shadow map reaches, in world units.")]
    public float ShadowDistance = 60f;

    [FoldoutGroup("Shadows", defaultOpen: false)]
    public float ShadowBias = 0.0015f;

    // Register on attach so edit mode is lit/shadowed by the scene light too, not just play mode.
    protected internal override void OnAttach()
    {
        registered = this;
    }

    protected internal override void OnDetach()
    {
        if (ReferenceEquals(registered, this))
            registered = null;
    }

    protected internal override void OnBegin()
    {
        registered = this;
    }

    public override void OnDrawGizmos(IGizmos gizmos) {
        gizmos.Color = new Vector3(1f, 0.9f, 0.5f);
        gizmos.DrawIcon(transform.Position, GizmoIcon.Light);
    }

    public override void OnDrawGizmosSelected(IGizmos gizmos) {
        // A short shaft of parallel rays pointing along -forward (the light's travel direction),
        // the universal "sun direction" gizmo.
        gizmos.Color = new Vector3(1f, 0.9f, 0.5f);
        Vector3 origin = transform.Position;
        Vector3 dir = transform.Forward;
        Vector3 right = transform.Right;
        Vector3 up = transform.Up;
        const float spread = 0.6f, length = 3f;

        foreach ((float ox, float oy) in new[] { (0f, 0f), (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f) }) {
            Vector3 start = origin + right * (ox * spread) + up * (oy * spread);
            gizmos.DrawRay(start, dir * length);
        }
    }
}
