namespace BallisticEngine;

// Aurora = the engine's HW-RT diffuse global-illumination solution (per-pixel ray trace → per-triangle
// radiance-cache for multi-bounce → screen-radiance probes → spatiotemporal denoise). Replaces the deleted
// probe-grid DDGI volume. Requires hardware ray tracing; without it GI is unavailable regardless of this toggle.
public sealed class AuroraVolume : VolumeComponent {
    [Tooltip("Master switch for Aurora global illumination. Off → direct lighting + IBL + AO + shadows only. " +
             "Requires hardware ray tracing; without it GI is unavailable regardless of this toggle.")]
    public readonly BoolParameter enabled = new(true);

    [Tooltip("Quality preset. High = 24 rays / oct 8 / 0 budget cap / 2 denoise; Balanced = 16 / 6 / 50k / 1 (default); " +
             "Performance = 8 / 6 / 25k / 1. Custom = honour the explicit dials below.")]
    public readonly EnumParameter<AuroraQuality> quality = new(AuroraQuality.Balanced);

    [Tooltip("Overall strength of the indirect (diffuse GI) contribution. 2 = the tuned default that gives visible " +
             "indirect bounce without washing the scene out.")]
    public readonly ClampedFloatParameter intensity = new(2f, 0f, 8f);

    [Tooltip("How much skylight enters on a trace ray that misses all geometry (a sealed interior stays dark).")]
    public readonly ClampedFloatParameter skyIntensity = new(1.5f, 0f, 4f);

    [Tooltip("Hemisphere rays traced per pixel. Higher = cleaner raw signal, more cost. Temporal accumulation + " +
             "ReSTIR reuse + denoise clean the rest. Only honoured when Quality = Custom; presets set it.")]
    [ShowIf(nameof(quality), AuroraQuality.Custom)]
    public readonly ClampedIntParameter rayCount = new(16, 1, 64);

    [Tooltip("Octahedral tile resolution per radiance-cache probe (oct × oct cells). Only honoured when Quality = Custom.")]
    [ShowIf(nameof(quality), AuroraQuality.Custom)]
    public readonly ClampedIntParameter probeOct = new(6, 4, 16);

    [Tooltip("Card-light records relit per frame (round-robin amortises the radiance cache; 0 = unlimited). " +
             "Only honoured when Quality = Custom.")]
    [ShowIf(nameof(quality), AuroraQuality.Custom)]
    public readonly ClampedIntParameter cardBudget = new(50000, 0, 500000);

    [Tooltip("À-trous spatial denoise iterations (0 = raw). Adaptive pass bumps it up on disocclusion. " +
             "Only honoured when Quality = Custom.")]
    [ShowIf(nameof(quality), AuroraQuality.Custom)]
    public readonly ClampedIntParameter denoisePasses = new(1, 0, 5);

    [Tooltip("Accumulate multi-bounce in the radiance cache — light bounces more than once with no extra primary rays.")]
    public readonly BoolParameter multiBounce = new(true);

    [Tooltip("How much the AmbientOcclusion volume's GTAO darkens the GI contact shading. DEFAULT 0: GTAO is " +
             "screen-space (ghosting under motion) and the RT trace already carries macro occlusion. 0 = none, 1 = full.")]
    public readonly ClampedFloatParameter aoStrength = new(0f, 0f, 1f);

    [Tooltip("DEBUG: replace the scene with the raw indirect irradiance E (what the GI gathers per pixel before " +
             "albedo/AO). Shows exactly what colour each region is sampling. Off for normal rendering.")]
    public readonly BoolParameter debugRawIndirect = new(false);
}
