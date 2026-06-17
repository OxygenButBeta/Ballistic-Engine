namespace BallisticEngine;

public sealed class Bloom : VolumeComponent {
    public readonly BoolParameter enabled = new(true);

    [Tooltip("How much of the bright, blurred bloom is added back over the scene.")]
    public readonly ClampedFloatParameter intensity = new(0.04f, 0f, 1f);

    [Tooltip("HDR luminance above which pixels start to bloom (soft knee).")]
    public readonly ClampedFloatParameter threshold = new(1f, 0f, 8f);

    [Tooltip("Soft-knee width below the threshold. Lower = harder cutoff (only bright spots bloom, " +
             "no scene-wide glow); higher = softer ramp-in.")]
    public readonly ClampedFloatParameter knee = new(0.5f, 0f, 2f);
}
