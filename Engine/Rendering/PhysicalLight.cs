
namespace BallisticEngine;

// Physical light helpers: colour temperature -> linear RGB, and the unit scale that ties the
// sun, punctual lights and IBL to the camera EV exposure. Everything the renderer multiplies
// into radiance ultimately balances against PostProcessSettings.ExposureEV, so these constants
// are the single place the "what is 1.0 of light" question is answered.
public static class PhysicalLight {
    // TRUE physical scale: lights live in real magnitudes in the HDR buffer (sun ~80000 lux,
    // a bulb ~1500 lm), and the EV100 exposure in the composite does the full camera job of
    // bringing that down to display range - exactly like metering a real scene. So this factor
    // is 1.0 (no pre-divide); it stays as the single global trim if every physical light ever
    // needs rebalancing against the IBL at once. Do NOT divide lights here AND apply EV - that
    // double-darkens the scene to near-black (the bug that made everything look unlit).
    public const float LuxToRadiance = 1.0f;

    // Punctual lights (point/spot) are authored in lumens, which are TINY next to the sun's
    // ~80000 lux: a physically-correct 1500 lm bulb is ~670x dimmer at the source and then loses
    // most of that to 1/dist^2, so under a sunlit EV it reads as black. This scale lifts a bulb's
    // candela into a range that's actually visible at the same exposure as the sun — it's the
    // "1 unit of punctual Intensity" calibration. Tuned so a default light (Intensity 1, ~1500 lm,
    // range 10) clearly lights nearby geometry without blowing out. Sun/IBL do NOT use it.
    public const float PunctualIntensityScale = 600f;

    // Approximate blackbody colour temperature (Kelvin) -> linear RGB, normalised so luminance
    // is ~1 (the intensity/illuminance carries brightness, temperature only carries hue).
    // Tanner Helland's piecewise fit, then sRGB->linear and luma-normalised. 6500K ~= white.
    public static Vector3 KelvinToRGB(float kelvin) {
        float t = System.Math.Clamp(kelvin, 1000f, 40000f) / 100f;

        float r, g, b;
        if (t <= 66f) {
            r = 1f;
            g = System.Math.Clamp(0.39008157f * System.MathF.Log(t) - 0.63184144f, 0f, 1f);
        } else {
            r = System.Math.Clamp(1.29293618f * System.MathF.Pow(t - 60f, -0.1332047592f), 0f, 1f);
            g = System.Math.Clamp(1.12989086f * System.MathF.Pow(t - 60f, -0.0755148492f), 0f, 1f);
        }
        if (t >= 66f) b = 1f;
        else if (t <= 19f) b = 0f;
        else b = System.Math.Clamp(0.54320679f * System.MathF.Log(t - 10f) - 1.19625409f, 0f, 1f);

        // sRGB gamma -> linear (the shader works in linear light).
        var srgb = new Vector3(r, g, b);
        var lin = new Vector3(SrgbToLinear(srgb.X), SrgbToLinear(srgb.Y), SrgbToLinear(srgb.Z));

        // Normalise so temperature only shifts hue, not brightness (luma == 1).
        float luma = Vector3.Dot(lin, new Vector3(0.2126f, 0.7152f, 0.0722f));
        return luma > 1e-4f ? lin / luma : lin;
    }

    static float SrgbToLinear(float c) =>
        c <= 0.04045f ? c / 12.92f : System.MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
}
