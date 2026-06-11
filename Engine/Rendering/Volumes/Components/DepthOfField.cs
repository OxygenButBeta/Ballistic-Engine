namespace BallisticEngine;

// Thin-lens depth of field. Physical controls so it reads like a real camera: the focal plane
// sits at focusDistance, and aperture (f-number) + focalLength set how shallow the field is.
// Off by default — a scene opts in by adding this override to its volume profile.
public sealed class DepthOfField : VolumeComponent {
    [Tooltip("Enable depth of field.")]
    public readonly BoolParameter enabled = new(false);

    [Tooltip("Distance to the in-focus plane, in metres.")]
    public readonly ClampedFloatParameter focusDistance = new(8f, 0.1f, 500f);

    [Tooltip("Lens focal length in metres (0.05 ~= 50mm). Larger = shallower depth of field.")]
    public readonly ClampedFloatParameter focalLength = new(0.05f, 0.01f, 0.3f);

    [Tooltip("Aperture f-number. Smaller = shallower depth of field and bigger bokeh.")]
    public readonly ClampedFloatParameter aperture = new(2.8f, 0.7f, 22f);

    [Tooltip("Maximum blur radius as a fraction of frame height (caps the bokeh size).")]
    public readonly ClampedFloatParameter maxBlur = new(0.03f, 0.005f, 0.1f);
}
