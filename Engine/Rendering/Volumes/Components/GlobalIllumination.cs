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
// Lumen performance preset. A tier is a (probeOct, cardBudget, denoisePasses) bundle picked for a frame-time
// target — integrate cost is ~oct² per probe per pixel, so the octahedral tile size is the dominant knob.
// Measured (Bistro exterior, RX 9070 XT): High ~130 FPS, Balanced ~174, Performance ~234. Custom = author each
// dial by hand (the tier no longer overrides probeOct/budget/denoisePasses).
public enum GiQuality { High, Balanced, Performance, Custom }

public sealed class GlobalIllumination : VolumeComponent {
    [Tooltip("Master switch for Lumen global illumination. Off → direct lighting + IBL + AO + shadows only. " +
             "Requires hardware ray tracing; without it GI is unavailable regardless of this toggle.")]
    public readonly BoolParameter enabled = new(true);

    [Tooltip("Performance preset. Sets probe octahedral resolution, card-relight budget, and denoise passes for a " +
             "frame-time target. High = max fidelity (oct 8). Balanced = the default (oct 6, ~3× cheaper than High " +
             "for near-identical quality). Performance = fastest (oct 4). Custom = honour the individual dials below.")]
    public readonly EnumParameter<GiQuality> quality = new(GiQuality.Balanced);

    [Tooltip("Overall strength of the indirect (diffuse GI) contribution. 1 = physical.")]
    public readonly ClampedFloatParameter intensity = new(1f, 0f, 4f);   // 1 = physical (was 2 to paper over the combine's extra /π — now fixed in LumenGi.hlsl)

    [Tooltip("How much skylight enters through open sky-visibility (a sealed interior stays dark regardless).")]
    public readonly ClampedFloatParameter skyIntensity = new(1.5f, 0f, 4f);

    [Tooltip("Hemisphere rays traced per pixel. Higher = less noise before denoise/temporal, more cost.")]
    public readonly ClampedIntParameter rayCount = new(16, 1, 16);

    [Tooltip("Octahedral tile resolution per probe (oct × oct cells). Dominant cost knob (~oct²). Only honoured " +
             "when Quality = Custom; the presets set it.")]
    [ShowIf(nameof(quality), GiQuality.Custom)]
    public readonly ClampedIntParameter probeOct = new(6, 4, 16);

    [Tooltip("Card-light records relit per frame (round-robin; 0 = unlimited). Only honoured when Quality = Custom.")]
    [ShowIf(nameof(quality), GiQuality.Custom)]
    public readonly ClampedIntParameter cardBudget = new(50000, 0, 400000);

    [Tooltip("Edge-aware spatial denoise iterations on the indirect (à-trous). Adaptive: this is the steady-state " +
             "count; the pass temporarily raises it on disocclusion (history miss). Only honoured when Quality = Custom.")]
    [ShowIf(nameof(quality), GiQuality.Custom)]
    public readonly ClampedIntParameter denoisePasses = new(1, 0, 5);

    [Tooltip("Accumulate multi-bounce in the surface-card radiance cache (light bounces more than once).")]
    public readonly BoolParameter multiBounce = new(true);

    [Tooltip("How much the AmbientOcclusion volume's GTAO darkens the GI's contact shading. DEFAULT 0: GTAO is " +
             "screen-space and dragged a dark 'ghost of nearby geometry' smudge under camera motion, and the RT " +
             "trace already carries macro occlusion (so it double-darkened). Raise only if a scene specifically " +
             "wants the extra contact term and tolerates the screen-space artifact (0 = none, 1 = full GTAO).")]
    public readonly ClampedFloatParameter aoStrength = new(0f, 0f, 1f);
}
