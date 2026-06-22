namespace BallisticEngine;

public sealed class Vignette : VolumeComponent {
    [Tooltip("Darkens the frame edges.")]
    public readonly ClampedFloatParameter intensity = new(0f, 0f, 1f);

    [Tooltip("1 = circular falloff, 0 = follows the frame aspect (oval).")]
    public readonly ClampedFloatParameter roundness = new(1f, 0f, 1f);

    [Tooltip("Colour the edges fade toward (usually black).")]
    public readonly ColorParameter color = new(Vector3.Zero);
}
