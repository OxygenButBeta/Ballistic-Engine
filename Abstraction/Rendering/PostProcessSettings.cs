namespace BallisticEngine;

// Tunables for the HDR -> display pipeline. Neutral by default: only exposure,
// ACES tonemapping and gamma always run; everything stylistic is opt-in so the
// calibrated PBR output isn't silently distorted.
public sealed class PostProcessSettings {
    public float Exposure { get; set; } = 1f;

    public bool BloomEnabled { get; set; } = true;
    public float BloomIntensity { get; set; } = 0.04f;
    // HDR threshold with a soft knee; values below it leak progressively less into bloom.
    public float BloomThreshold { get; set; } = 1f;

    public bool SSAOEnabled { get; set; } = true;
    public float SSAORadius { get; set; } = 0.5f; // world units
    public float SSAOIntensity { get; set; } = 1f;

    // 1 = off. Offscreen targets are recreated when this changes.
    public int MsaaSamples { get; set; } = 4;

    // Stylistic extras, all neutral/off by default.
    public float Contrast { get; set; } = 1f;
    public float Saturation { get; set; } = 1f;
    public float VignetteStrength { get; set; }
    public float FilmGrain { get; set; }
    public float Sharpen { get; set; }
}
