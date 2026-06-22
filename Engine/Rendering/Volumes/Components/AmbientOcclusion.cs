namespace BallisticEngine;

public sealed class AmbientOcclusion : VolumeComponent {
    public readonly BoolParameter enabled = new(true);

    [Tooltip("Sample budget. Low (2×4) is cheapest + noisiest; Ultra (6×12) is reference quality. " +
             "TAA + the bilateral blur keep even Low usable.")]
    public readonly EnumParameter<AoQuality> quality = new(AoQuality.Medium);

    [Tooltip("Render resolution fraction. Half (1/4 the pixels) is the default; Quarter is cheapest, Full sharpest. " +
             "The AO is bilinear-upsampled when lighting samples it.")]
    public readonly EnumParameter<AoResolution> resolution = new(AoResolution.Half);

    [Tooltip("World-space sampling radius. Larger reads architectural crevices (window recesses, cornices, arches); " +
             "smaller suits props.")]
    public readonly ClampedFloatParameter radius = new(1.75f, 0.1f, 5f);

    [Tooltip("How dark the occlusion gets. GTAO is physically normalized, so 1 is the neutral strength.")]
    public readonly ClampedFloatParameter intensity = new(1.0f, 0f, 3f);

    [Header("Advanced")]
    [FoldoutGroup("Advanced")]
    [Tooltip("Contrast/falloff exponent applied to the occlusion. 1 = linear; higher deepens contact darkening.")]
    public readonly ClampedFloatParameter power = new(1.0f, 0.5f, 4f);

    [FoldoutGroup("Advanced")]
    [Tooltip("Assumed occluder thickness in metres. Thin lets light leak past railings/foliage; " +
             "thick treats them as solid walls.")]
    public readonly ClampedFloatParameter thickness = new(0.25f, 0.05f, 2f);

    [FoldoutGroup("Advanced")]
    [Tooltip("Jimenez albedo-aware multi-bounce: keeps dark crevices from crushing to black by re-introducing " +
             "the light that would bounce within them. On = the physically-grounded look.")]
    public readonly BoolParameter multiBounce = new(true);
}
