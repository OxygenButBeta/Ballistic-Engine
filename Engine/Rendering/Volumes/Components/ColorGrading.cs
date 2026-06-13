namespace BallisticEngine;

// Stylistic grade components, all neutral/off by default so the calibrated PBR output
// isn't silently distorted. Split per effect, Unity-style, so a profile only carries
// the looks it actually overrides.

public sealed class ColorAdjustments : VolumeComponent {
    [Tooltip("Midtone contrast around mid-grey.")]
    // Default 1.15 (not 1.0): the ACES output crushes the scene into the midtones — Sun Temple
    // spanned only luma 72-136, no deep shadows or bright highlights, reading flat/hazy. A modest
    // contrast expansion around 0.5 restores the full tonal range and the photographic/UE5 punch
    // that lets the GI bounce + material colour show (verified against the luma histogram: range
    // 64 -> 110). A scene that wants pure-neutral can still set it back to 1.0.
    public readonly ClampedFloatParameter contrast = new(1.15f, 0.5f, 2f);

    [Tooltip("Overall colour saturation.")]
    // Default 1.1 (slightly rich, not neutral): a modest saturation lift gives the punchy-but-natural
    // colour of a UE5/filmic render — the red marble, gold, foliage read more vividly without going
    // garish (Bistro mean sat 57 -> 62). Pairs with the 1.15 contrast. Set 1.0 for pure-neutral.
    public readonly ClampedFloatParameter saturation = new(1.1f, 0f, 2f);
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
