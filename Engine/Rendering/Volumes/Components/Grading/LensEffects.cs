namespace BallisticEngine;

public sealed class LensEffects : VolumeComponent {
    [Tooltip("Lateral chromatic aberration: RGB split that grows toward the frame edge.")]
    public readonly ClampedFloatParameter chromaticAberration = new(0f, 0f, 5f);

    [Tooltip("Lens warp: positive = barrel, negative = pincushion.")]
    public readonly ClampedFloatParameter distortion = new(0f, -1f, 1f);
}
