
namespace BallisticEngine;

public class ProceduralSky : SceneBehaviour {
    public static ProceduralSky Active { get; private set; }

    public float Exposure { get; set; } = 1f;

    [Header("Atmosphere")]
    [Range(0f, 4f)]
    public float AirDensity { get; set; } = 1f;

    [Range(0f, 8f)]
    public float Haze { get; set; } = 1f;

    [Range(0f, 0.99f)]
    public float HazeAnisotropy { get; set; } = 0.8f;

    [Range(0f, 3f)]
    public float OzoneDensity { get; set; } = 1f;

    public Vector3 GroundColor { get; set; } = new(0.25f, 0.24f, 0.22f);

    [Range(1f, 4f)]
    public float MultipleScattering { get; set; } = 2.2f;

    [Header("Sun disk")]
    [Range(0f, 4f)]
    public float SunDiskIntensity { get; set; } = 1f;

    public int Resolution { get; set; } = 256;

    [Header("Volumetric clouds")] public bool CloudsEnabled { get; set; } = true;

    [Range(0f, 1f)]
    public float CloudCoverage { get; set; } = 0.35f;

    [Range(0.1f, 4f)]
    public float CloudDensity { get; set; } = 1f;

    public float CloudAltitude { get; set; } = 1500f;

    public float CloudThickness { get; set; } = 2600f;

    [Range(0.25f, 4f)]
    public float CloudScale { get; set; } = 1f;

    [Range(0f, 1f)]
    public float CloudDetail { get; set; } = 0.5f;

    [Range(0f, 2f)]
    public float CloudAmbient { get; set; } = 1f;

    public float CloudWindSpeed { get; set; } = 5f;

    [Range(0f, 360f)]
    public float CloudWindDirection { get; set; } = 0f;

    [Range(0f, 10f)]
    public float CloudUpdateInterval { get; set; } = 0f;

    [Header("Cirrus")]
    [Range(0f, 1f)]
    public float CirrusCoverage { get; set; } = 0.15f;

    [Header("Night")]
    [Range(0f, 4f)]
    public float StarIntensity { get; set; } = 1f;

    public Vector3 SunTransmittance(Vector3 sunDirection) {
        const float Rp = 6360e3f, Ra = 6460e3f, Hr = 8500f, Hm = 1200f;
        const int Steps = 8;
        Vector3 betaR = new(5.802e-6f, 13.558e-6f, 33.1e-6f);
        const float betaM = 3.996e-6f;
        Vector3 betaO = new(0.650e-6f, 1.881e-6f, 0.085e-6f);

        if (sunDirection.LengthSquared() < 1e-8f)
            return Vector3.One;
        Vector3 dir = sunDirection.Normalized();
        Vector3 origin = new(0f, Rp + 500f, 0f);

        float b = Vector3.Dot(origin, dir);
        float exit = -b + MathF.Sqrt(MathF.Max(b * b - (origin.LengthSquared() - Ra * Ra), 0f));
        float seg = exit / Steps;

        Vector3 depths = Vector3.Zero;
        for (var j = 0; j < Steps; j++) {
            Vector3 p = origin + dir * ((j + 0.5f) * seg);
            float h = MathF.Max(p.Length() - Rp, 0f);
            depths.X += MathF.Exp(-h / Hr) * seg;
            depths.Y += MathF.Exp(-h / Hm) * seg;
            depths.Z += MathF.Max(0f, 1f - MathF.Abs(h - 25000f) / 15000f) * seg;
        }

        Vector3 tau = betaR * AirDensity * depths.X
                    + new Vector3(betaM * 1.11f * Haze * depths.Y)
                    + betaO * OzoneDensity * depths.Z;
        return new Vector3(MathF.Exp(-tau.X), MathF.Exp(-tau.Y), MathF.Exp(-tau.Z));
    }

    protected internal override void OnAttach() {
        Active = this;
    }

    protected internal override void OnDetach() {
        if (ReferenceEquals(Active, this))
            Active = null;
    }
}
