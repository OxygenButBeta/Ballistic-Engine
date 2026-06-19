
namespace BallisticEngine;

// Volumetric height fog + sun scattering (god-rays). Off by default — it's an atmospheric,
// scene-dependent look. The medium is physical: exponential height fog whose extinction
// hides the scene behind it, in-scattering the atmosphere-attenuated sun (golden at dusk,
// gone at night when a ProceduralSky drives the scene) and the baked sky's average
// radiance as skylight — so the fog always matches the sky and clouds above it.
//
// SUPERSEDED by [[VolumetricLighting]] (fog + independent god rays + dust). This type is kept and stays
// fully functional (the VolumePostProcessing bridge still reads it, so existing scenes/profiles using it
// render unchanged), but it is HIDDEN from the editor's Add Override menu so new content uses the unified
// override and the two can't both be added by hand.
[Component(HideFromAddMenu = true)]
public sealed class VolumetricFog : VolumeComponent {
    public readonly BoolParameter enabled = new(false);

    [Tooltip("Master strength: fades the whole fog out below 1, boosts only the glow above 1.")]
    public readonly ClampedFloatParameter intensity = new(1f, 0f, 4f);

    [Tooltip("Fog extinction at the base height, 1/m. 0.002 ≈ 2 km visibility (light haze); 0.01 = mist; 0.05 = fog bank.")]
    public readonly ClampedFloatParameter density = new(0.002f, 0f, 0.2f);

    [Tooltip("How fast the fog thins with altitude, 1/m (0 = uniform). 0.04 ≈ a 25 m-thick ground layer.")]
    public readonly ClampedFloatParameter heightFalloff = new(0.04f, 0f, 0.5f);

    [Tooltip("World height below which the fog is at full density.")]
    public readonly FloatParameter baseHeight = new(0f);

    [Tooltip("Sunlight in-scatter multiplier (1 = physical balance against the sky).")]
    public readonly ClampedFloatParameter scattering = new(1f, 0f, 4f);

    [Tooltip("Skylight in-scatter multiplier (1 = physical balance). Drives the fog's ambient color from the baked sky.")]
    public readonly ClampedFloatParameter ambientScatter = new(1f, 0f, 2f);

    [Tooltip("Phase anisotropy. Higher = tighter, brighter shafts toward the sun.")]
    public readonly ClampedFloatParameter anisotropy = new(0.7f, 0f, 0.95f);

    [Tooltip("Extra blaze around the sun disk seen through the fog.")]
    public readonly ClampedFloatParameter sunGlow = new(0.3f, 0f, 2f);

    [Tooltip("Tightness of the sun-disk glow (higher = smaller/hotter).")]
    public readonly ClampedFloatParameter sunGlowSharpness = new(48f, 1f, 128f);

    [Tooltip("Raymarch samples inside shadow-map range (cost vs banding).")]
    public readonly ClampedIntParameter stepCount = new(48, 8, 128);

    [Tooltip("Metres the shadowed march reaches; beyond it the fog continues analytically (sun counted lit).")]
    public readonly ClampedFloatParameter maxDistance = new(120f, 10f, 400f);

    [Tooltip("Temporal history weight (smoother/laggier).")]
    public readonly ClampedFloatParameter feedback = new(0.9f, 0.5f, 0.98f);

    [Tooltip("Colour grade on the fog's in-scatter (extinction stays neutral).")]
    public readonly ColorParameter tint = new(Vector3.One);
}
