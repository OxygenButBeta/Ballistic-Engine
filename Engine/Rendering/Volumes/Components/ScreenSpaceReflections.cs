namespace BallisticEngine;

public sealed class ScreenSpaceReflections : VolumeComponent {
    public readonly BoolParameter enabled = new(true);

    [Tooltip("Strength of screen-space reflections on smooth surfaces.")]
    public readonly ClampedFloatParameter intensity = new(1f, 0f, 2f);
}
