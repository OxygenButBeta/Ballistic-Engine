namespace BallisticEngine;

public sealed class ScreenSpaceReflections : VolumeComponent {
    public readonly BoolParameter enabled = new(true);

    [Tooltip("Strength of reflections on smooth surfaces.")]
    public readonly ClampedFloatParameter intensity = new(1f, 0f, 2f);

    [Tooltip("Reflection technique: Screen Space (fast, screen-bounded) or Ray Traced (DX12 + DXR; " +
             "off-screen geometry + sky reflect correctly, no screen-edge fade). RT falls back to SSR " +
             "on GPUs without ray tracing.")]
    public readonly EnumParameter<ReflectionMode> mode = new(ReflectionMode.ScreenSpace);
}
