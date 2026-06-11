namespace BallisticEngine;

// Stylistic grade components, all neutral/off by default so the calibrated PBR output
// isn't silently distorted. Split per effect, Unity-style, so a profile only carries
// the looks it actually overrides.

public sealed class ColorAdjustments : VolumeComponent {
    [Tooltip("Midtone contrast around mid-grey.")]
    public readonly ClampedFloatParameter contrast = new(1f, 0.5f, 2f);

    [Tooltip("Overall colour saturation.")]
    public readonly ClampedFloatParameter saturation = new(1f, 0f, 2f);
}

public sealed class Vignette : VolumeComponent {
    [Tooltip("Darkens the frame edges.")]
    public readonly ClampedFloatParameter intensity = new(0f, 0f, 1f);

    [Tooltip("1 = circular falloff, 0 = follows the frame aspect (oval).")]
    public readonly ClampedFloatParameter roundness = new(1f, 0f, 1f);

    [Tooltip("Colour the edges fade toward (usually black).")]
    public readonly ColorParameter color = new(OpenTK.Mathematics.Vector3.Zero);
}

public sealed class LensEffects : VolumeComponent {
    [Tooltip("Lateral chromatic aberration: RGB split that grows toward the frame edge.")]
    public readonly ClampedFloatParameter chromaticAberration = new(0f, 0f, 5f);

    [Tooltip("Lens warp: positive = barrel, negative = pincushion.")]
    public readonly ClampedFloatParameter distortion = new(0f, -1f, 1f);
}

public sealed class FilmGrain : VolumeComponent {
    [Tooltip("Adds animated film grain.")]
    public readonly ClampedFloatParameter intensity = new(0f, 0f, 0.2f);
}

public sealed class Sharpening : VolumeComponent {
    [Tooltip("Unsharp-mask sharpening on the final image.")]
    public readonly ClampedFloatParameter intensity = new(0f, 0f, 2f);
}
