namespace BallisticEngine;

// Global illumination override — the product control for Lumen V2 (the HW-RT GI/reflection stack). One
// product-facing mode (plan §Target Shape): when enabled, screen traces feed near-field bounce, hardware RT
// handles off-screen hits, and a per-triangle surface-card radiance cache supplies stable, multi-bounce
// indirect. There is NO screen-space fallback — without hardware ray tracing the GI is simply unavailable
// (the renderer hard-gates on it), so this override only takes effect on RT-capable hardware.
//
// Defaults mirror PostProcessSettings (so a scene with no GI volume still gets Lumen on at the engine
// defaults — VolumeManager seeds the stack from those defaults). Drives the DX12 Lumen GI pass via
// VolumePostProcessing.Apply → PostProcessSettings; the BALLISTIC_DX12_LUMEN_* env doors still override for
// A/B diagnostics.
public sealed class GlobalIllumination : VolumeComponent {
    [Tooltip("Master switch for Lumen global illumination. Off → direct lighting + IBL + AO + shadows only. " +
             "Requires hardware ray tracing; without it GI is unavailable regardless of this toggle.")]
    public readonly BoolParameter enabled = new(true);

    [Tooltip("Overall strength of the indirect (diffuse GI) contribution. 1 = physical.")]
    public readonly ClampedFloatParameter intensity = new(2f, 0f, 4f);

    [Tooltip("How much skylight enters through open sky-visibility (a sealed interior stays dark regardless).")]
    public readonly ClampedFloatParameter skyIntensity = new(1.5f, 0f, 4f);

    [Tooltip("Hemisphere rays traced per pixel. Higher = less noise before denoise/temporal, more cost.")]
    public readonly ClampedIntParameter rayCount = new(16, 1, 16);

    [Tooltip("Edge-aware spatial denoise iterations on the indirect (à-trous). 0 = raw (noisy).")]
    public readonly ClampedIntParameter denoisePasses = new(3, 0, 5);

    [Tooltip("Accumulate multi-bounce in the surface-card radiance cache (light bounces more than once).")]
    public readonly BoolParameter multiBounce = new(true);

    [Tooltip("How much the AmbientOcclusion volume's GTAO darkens the GI's contact shading. DEFAULT 0: GTAO is " +
             "screen-space and dragged a dark 'ghost of nearby geometry' smudge under camera motion, and the RT " +
             "trace already carries macro occlusion (so it double-darkened). Raise only if a scene specifically " +
             "wants the extra contact term and tolerates the screen-space artifact (0 = none, 1 = full GTAO).")]
    public readonly ClampedFloatParameter aoStrength = new(0f, 0f, 1f);
}
