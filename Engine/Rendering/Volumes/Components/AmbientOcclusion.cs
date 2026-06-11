namespace BallisticEngine;

public sealed class AmbientOcclusion : VolumeComponent {
    public readonly BoolParameter enabled = new(true);

    [Tooltip("World-space sampling radius. Larger reads architectural crevices; smaller suits props.")]
    public readonly ClampedFloatParameter radius = new(1.75f, 0.1f, 5f);

    [Tooltip("How dark the occlusion gets.")]
    public readonly ClampedFloatParameter intensity = new(1.3f, 0f, 3f);
}
