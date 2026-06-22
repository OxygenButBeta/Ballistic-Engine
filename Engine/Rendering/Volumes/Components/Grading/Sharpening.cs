namespace BallisticEngine;

public sealed class Sharpening : VolumeComponent {
    [Tooltip("Unsharp-mask sharpening on the final image.")]
    public readonly ClampedFloatParameter intensity = new(0f, 0f, 2f);
}
