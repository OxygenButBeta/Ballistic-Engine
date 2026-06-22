namespace BallisticEngine;

public sealed class FilmGrain : VolumeComponent {
    [Tooltip("Adds animated film grain.")]
    public readonly ClampedFloatParameter intensity = new(0f, 0f, 0.2f);
}
