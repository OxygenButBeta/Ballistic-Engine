namespace BallisticEngine;

public class DirectionalLight : Behaviour
{
    static DirectionalLight registered;

    public static DirectionalLight Instance =>
        registered is { IsActive: true } ? registered : null;

    public static void Clear() => registered = null;

    public Vector3 AmbientLight => _ambientColor * ambientIntensity;
    Vector3 _ambientColor = new(0.35f, 0.40f, 0.45f);

    [Header("Lighting")]
    [Range(0f, 2f)]
    public float ambientIntensity = .3f;

    [Range(0f, 150000f)]
    public float Illuminance = 80000f;

    [Range(1500f, 12000f)]
    public float ColorTemperature = 5500f;

    public Vector3 PhysicalColor =>
        PhysicalLight.KelvinToRGB(ColorTemperature) * (Illuminance * PhysicalLight.LuxToRadiance);

    public Vector3 LightColor => PhysicalColor;

    [Range(0.1f, 10f)]
    public float AngularDiameter = 0.53f;

    [FoldoutGroup("Shadows", defaultOpen: false)]
    [Tooltip("How far from the camera the shadow map reaches, in world units.")]
    public float ShadowDistance = 60f;

    [FoldoutGroup("Shadows", defaultOpen: false)]
    public float ShadowBias = 0.0015f;

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
