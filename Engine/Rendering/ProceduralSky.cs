using OpenTK.Mathematics;

namespace BallisticEngine;

// Physically-based procedural sky (Unity HDRP-style "Physically Based Sky"): a single-
// scattering Rayleigh + Mie + ozone atmosphere driven by the scene's DirectionalLight,
// baked into a cubemap whenever the sun or these parameters change. The baked cubemap then
// feeds the exact same paths as an HDRI skybox - the sky draw, the IBL irradiance/specular
// bake, SSGI's sky fallback - so the whole scene relights when the sun moves. No HDRI needed.
//
// Takes precedence over an asset Skybox while active. Output is in the engine's physical
// light scale (the sun's lux drives the sky luminance), so EV exposure treats it correctly.
public class ProceduralSky : SceneBehaviour {
    public static ProceduralSky Active { get; private set; }

    // Artistic luminance scale on the whole sky (baked into the cubemap; 1 = physical).
    public float Exposure { get; set; } = 1f;

    [Header("Atmosphere")]
    // Rayleigh density multiplier: 1 = Earth air. More = bluer/denser sky, redder sunsets.
    [Range(0f, 4f)]
    public float AirDensity { get; set; } = 1f;

    // Mie (aerosol) multiplier: 0 = crystal clear, 1 = clear day, 3+ = hazy/humid.
    [Range(0f, 8f)]
    public float Haze { get; set; } = 1f;

    // Mie phase anisotropy: how tightly haze glow hugs the sun.
    [Range(0f, 0.99f)]
    public float HazeAnisotropy { get; set; } = 0.8f;

    // Ozone absorption multiplier: gives twilight its deep blue; 0 disables.
    [Range(0f, 3f)]
    public float OzoneDensity { get; set; } = 1f;

    // What the virtual planet surface reflects below the horizon (feeds the IBL's lower
    // hemisphere, i.e. the ground-bounce ambient).
    public Vector3 GroundColor { get; set; } = new(0.25f, 0.24f, 0.22f);

    // Cheap multiple-scattering approximation: single scattering alone underestimates sky
    // brightness ~2-3x (the energy that bounces around the air more than once). 1 = off.
    [Range(1f, 4f)]
    public float MultipleScattering { get; set; } = 2.2f;

    [Header("Sun disk")]
    // Brightness of the VISIBLE disk texels only (direct lighting comes from the analytic
    // sun). Affects how hard the disk blooms and reflects.
    [Range(0f, 4f)]
    public float SunDiskIntensity { get; set; } = 1f;

    // Cube face resolution of the baked sky (256 keeps cloud shapes readable; a gradient-only
    // sky with clouds off can drop to 128).
    public int Resolution { get; set; } = 256;

    [Header("Volumetric clouds")]
    // Raymarched cumulus layer baked into the same cubemap, so clouds show up in
    // reflections, IBL ambient and SSGI exactly like the clear-sky gradient does.
    public bool CloudsEnabled { get; set; } = true;

    // Sky fraction the clouds occupy: 0 = clear, ~0.5 = scattered cumulus, 1 = overcast.
    [Range(0f, 1f)]
    public float CloudCoverage { get; set; } = 0.45f;

    // Extinction multiplier inside the clouds: low = wispy and translucent, high = dense
    // cauliflower cores with hard self-shadowing.
    [Range(0.1f, 4f)]
    public float CloudDensity { get; set; } = 1f;

    // Altitude of the cloud base above sea level, meters.
    public float CloudAltitude { get; set; } = 1500f;

    // Vertical extent of the cloud layer, meters.
    public float CloudThickness { get; set; } = 2600f;

    // Horizontal feature size multiplier: bigger = larger, calmer formations.
    [Range(0.25f, 4f)]
    public float CloudScale { get; set; } = 1f;

    // How strongly high-frequency noise erodes the cloud edges (0 = blobby, 1 = wispy).
    [Range(0f, 1f)]
    public float CloudDetail { get; set; } = 0.5f;

    // Skylight reaching the cloud interior; raise to lift the shadowed undersides.
    [Range(0f, 2f)]
    public float CloudAmbient { get; set; } = 1f;

    // Wind drifting the cloud field, m/s, blowing toward CloudWindDirection.
    public float CloudWindSpeed { get; set; } = 5f;

    // Compass direction the wind blows toward, degrees (0 = +Z, 90 = +X).
    [Range(0f, 360f)]
    public float CloudWindDirection { get; set; } = 0f;

    // Seconds between animated re-bakes while the wind moves the clouds. 0 = static clouds
    // (bake on parameter change only). Every re-bake also re-convolves the IBL, so short
    // intervals cost real frame time at high Resolution.
    [Range(0f, 10f)]
    public float CloudUpdateInterval { get; set; } = 0f;

    [Header("Cirrus")]
    // Thin icy streak sheet near 7.5 km, aligned with the wind and drifting ~2.5x faster
    // (jet stream). Baked into the same cubemap, so reflections and the IBL see it. 0 = clear.
    [Range(0f, 1f)]
    public float CirrusCoverage { get; set; } = 0.3f;

    [Header("Night")]
    // Procedural starfield fading in once the sun drops a few degrees below the horizon.
    // Artistic radiance scale (a physical starfield is too dim to meter against engine
    // lights). Stars are baked texels: Resolution >= 512 keeps them round.
    [Range(0f, 4f)]
    public float StarIntensity { get; set; } = 1f;

    // Atmospheric transmittance toward the sun from ground level - the same Rayleigh/Mie/
    // ozone integral Sky_Procedural.glsl marches (constants must stay in sync with it). The
    // renderer multiplies this into the directional light and the volumetric fog while a
    // ProceduralSky is active, so geometry and fog redden/dim with the SAME atmosphere the
    // sky shows: golden-hour light on meshes, the sun fading out below the horizon.
    public Vector3 SunTransmittance(Vector3 sunDirection) {
        const float Rp = 6360e3f, Ra = 6460e3f, Hr = 8500f, Hm = 1200f;
        const int Steps = 8;
        Vector3 betaR = new(5.802e-6f, 13.558e-6f, 33.1e-6f);
        const float betaM = 3.996e-6f;
        Vector3 betaO = new(0.650e-6f, 1.881e-6f, 0.085e-6f);

        if (sunDirection.LengthSquared < 1e-8f)
            return Vector3.One;
        Vector3 dir = sunDirection.Normalized();
        Vector3 origin = new(0f, Rp + 500f, 0f);

        // Distance to the atmosphere top (the ray may dip through dense low air first -
        // below the horizon that optical depth explodes and transmittance goes to ~0,
        // which is exactly the wanted "sun has set" behaviour).
        float b = Vector3.Dot(origin, dir);
        float exit = -b + MathF.Sqrt(MathF.Max(b * b - (origin.LengthSquared - Ra * Ra), 0f));
        float seg = exit / Steps;

        Vector3 depths = Vector3.Zero; // (rayleigh, mie, ozone) integrated path densities
        for (var j = 0; j < Steps; j++) {
            Vector3 p = origin + dir * ((j + 0.5f) * seg);
            float h = MathF.Max(p.Length - Rp, 0f);
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
