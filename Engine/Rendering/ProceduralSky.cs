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

    protected internal override void OnAttach() {
        Active = this;
    }

    protected internal override void OnDetach() {
        if (ReferenceEquals(Active, this))
            Active = null;
    }
}
