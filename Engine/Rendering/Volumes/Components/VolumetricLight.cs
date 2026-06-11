using OpenTK.Mathematics;

namespace BallisticEngine;

// Volumetric sun scattering (god-rays). Off by default — it's an atmospheric, scene-dependent look.
public sealed class VolumetricLight : VolumeComponent {
    public readonly BoolParameter enabled = new(false);

    [Tooltip("Master strength of the sun shafts.")]
    public readonly ClampedFloatParameter intensity = new(1f, 0f, 4f);

    [Tooltip("Depth-falloff shape of the fog (near vs far weighting), not brightness.")]
    public readonly ClampedFloatParameter density = new(0.06f, 0f, 0.5f);

    [Tooltip("Shaft brightness.")]
    public readonly ClampedFloatParameter scattering = new(2.5f, 0f, 8f);

    [Tooltip("Phase anisotropy. Higher = tighter forward shafts.")]
    public readonly ClampedFloatParameter anisotropy = new(0.76f, 0f, 0.95f);

    [Tooltip("Extra blaze around the sun disk.")]
    public readonly ClampedFloatParameter sunGlow = new(0.3f, 0f, 2f);

    [Tooltip("Tightness of the sun-disk glow (higher = smaller/hotter).")]
    public readonly ClampedFloatParameter sunGlowSharpness = new(48f, 1f, 128f);

    [Tooltip("Min scatter when not looking at the sun (0 = sun-facing only, 1 = uniform).")]
    public readonly ClampedFloatParameter ambientFloor = new(0.25f, 0f, 1f);

    [Tooltip("Raymarch samples (cost vs banding).")]
    public readonly ClampedIntParameter stepCount = new(48, 8, 128);

    [Tooltip("Metres the march reaches.")]
    public readonly ClampedFloatParameter maxDistance = new(120f, 10f, 400f);

    [Tooltip("Temporal history weight (smoother/laggier).")]
    public readonly ClampedFloatParameter feedback = new(0.9f, 0.5f, 0.98f);

    [Tooltip("Shaft colour grade.")]
    public readonly ColorParameter tint = new(Vector3.One);
}
