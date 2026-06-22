namespace BallisticEngine;

public sealed class ColorAdjustments : VolumeComponent {
    [Tooltip("Midtone contrast around mid-grey.")]
    public readonly ClampedFloatParameter contrast = new(1f, 0.5f, 2f);

    [Tooltip("Overall colour saturation.")]
    public readonly ClampedFloatParameter saturation = new(1f, 0f, 2f);
}
