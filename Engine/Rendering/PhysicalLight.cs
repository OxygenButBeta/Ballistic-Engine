
namespace BallisticEngine;

public static class PhysicalLight {
    public const float LuxToRadiance = 1.0f;

    public const float PunctualIntensityScale = 600f;

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

        var srgb = new Vector3(r, g, b);
        var lin = new Vector3(SrgbToLinear(srgb.X), SrgbToLinear(srgb.Y), SrgbToLinear(srgb.Z));

        float luma = Vector3.Dot(lin, new Vector3(0.2126f, 0.7152f, 0.0722f));
        return luma > 1e-4f ? lin / luma : lin;
    }

    static float SrgbToLinear(float c) =>
        c <= 0.04045f ? c / 12.92f : System.MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
}
