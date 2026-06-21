namespace BallisticEngine;

// DDGI global illumination override — the product control for the world-space probe-grid GI (the HW-RT diffuse
// stack that replaced Lumen V2). When enabled, a uniform 3D probe grid covers the scene; each probe traces the
// sphere with hardware ray tracing and stores octahedral irradiance + visibility moments, and every pixel
// trilinearly gathers the probes around it. View-independent (one EMA feedback loop) — no screen-space temporal
// or denoise. There is NO screen-space fallback: without hardware ray tracing the GI is unavailable (the
// renderer hard-gates on it), so this override only takes effect on RT-capable hardware.
//
// Defaults mirror PostProcessSettings (a scene with no GI volume still gets DDGI on at the engine defaults).
// Drives the DX12 DDGI pass via VolumePostProcessing.Apply → PostProcessSettings; the BALLISTIC_DX12_DDGI_*
// env doors still override each dial for A/B.

// Probe-grid density preset. A tier just picks the grid resolution (X×Y×Z) for a frame-time target — relight
// cost is per-probe, so probe count is the dominant knob. Custom = author the three counts by hand.
public enum GiQuality { High, Balanced, Performance, Custom }

// Where the probe grid gets its bounds. SceneAuto fits the whole scene's world AABB (default — the
// historical behaviour). Volume confines the grid to THIS GI volume's box, so a far stray object can't
// inflate the AABB and starve the room of probes (the cause of visible probe blobs / corner stepping /
// flickering buried probes in scenes with distant geometry). Volume mode needs a LOCAL volume (IsGlobal
// off) with a box drawn around the lit area; a global volume has no box → falls back to SceneAuto.
public enum GiBoundsMode { SceneAuto, Volume }

public sealed class GiVolume : VolumeComponent {
    [Tooltip("Master switch for DDGI global illumination. Off → direct lighting + IBL + AO + shadows only. " +
             "Requires hardware ray tracing; without it GI is unavailable regardless of this toggle.")]
    public readonly BoolParameter enabled = new(true);

    [Tooltip("Probe-grid density preset. High = 24×12×24, Balanced = 16×8×16 (default), Performance = 10×6×10. " +
             "Custom = honour the explicit grid counts below.")]
    public readonly EnumParameter<GiQuality> quality = new(GiQuality.Balanced);

    [Tooltip("Probe-grid bounds. SceneAuto = fit the whole scene AABB (default). Volume = confine the grid to " +
             "THIS volume's box (set IsGlobal off + size the box around the lit area) so distant geometry can't " +
             "starve the room of probes — fixes probe blobs / corner stepping / flickering buried probes.")]
    public readonly EnumParameter<GiBoundsMode> boundsMode = new(GiBoundsMode.SceneAuto);

    [Tooltip("Overall strength of the indirect (diffuse GI) contribution. 1 = physical.")]
    public readonly ClampedFloatParameter intensity = new(1f, 0f, 4f);

    [Tooltip("How much skylight enters on a probe ray that misses all geometry (a sealed interior stays dark).")]
    public readonly ClampedFloatParameter skyIntensity = new(1.5f, 0f, 4f);

    [Tooltip("Probe irradiance temporal blend (the single feedback loop). Lower = smoother/slower to react; the " +
             "pass auto-snaps it up on a large lighting change (hysteresis).")]
    public readonly ClampedFloatParameter emaAlpha = new(0.05f, 0.01f, 1f);

    [Tooltip("Probe grid X count. Only honoured when Quality = Custom; the presets set it.")]
    [ShowIf(nameof(quality), GiQuality.Custom)]
    public readonly ClampedIntParameter gridX = new(16, 2, 64);

    [Tooltip("Probe grid Y count. Only honoured when Quality = Custom.")]
    [ShowIf(nameof(quality), GiQuality.Custom)]
    public readonly ClampedIntParameter gridY = new(8, 2, 64);

    [Tooltip("Probe grid Z count. Only honoured when Quality = Custom.")]
    [ShowIf(nameof(quality), GiQuality.Custom)]
    public readonly ClampedIntParameter gridZ = new(16, 2, 64);

    [Tooltip("Feed the previous frame's probe irradiance back at each relight hit — cheap multi-bounce (light " +
             "bounces more than once) with no extra rays.")]
    public readonly BoolParameter multiBounce = new(true);

    [Tooltip("Chebyshev visibility test: reject probes occluded from the surface (the light-leak fix that lets " +
             "the grid work in enclosed geometry). Off → trilinear only (leaks through thin walls).")]
    public readonly BoolParameter visibility = new(true);

    [Tooltip("Surface normal bias (metres) when a pixel gathers the probes: push the sample point off the surface " +
             "along its normal. Higher = less self-shadow/acne but more leak through thin walls; lower = tighter " +
             "contact but risk of self-occlusion darkening. Default 0.2.")]
    public readonly ClampedFloatParameter normalBias = new(0.2f, 0f, 2f);

    // NOTE: "reflections sample the GI" is controlled by the Reflections volume's `sampleRadianceCache` toggle
    // (it feeds fx.DdgiReflections), NOT here — a duplicate dial on this component was dead (never plumbed) and
    // was removed to avoid a knob that silently did nothing.

    [Tooltip("How much the AmbientOcclusion volume's GTAO darkens the GI's contact shading. DEFAULT 0: GTAO is " +
             "screen-space (ghosting under motion) and the RT trace already carries macro occlusion. 0 = none, 1 = full.")]
    public readonly ClampedFloatParameter aoStrength = new(0f, 0f, 1f);

    [Tooltip("DEBUG: draw every probe as a world-space sphere tinted by its stored irradiance, depth-tested against " +
             "the scene. Lets you SEE the probe grid + what each probe sampled. Off for normal rendering.")]
    public readonly BoolParameter debugProbes = new(false);

    [Tooltip("DEBUG: replace the scene with the raw indirect irradiance E (what the GI gathers per pixel before " +
             "albedo/AO). Shows exactly what color each region is sampling. Off for normal rendering.")]
    public readonly BoolParameter debugRawIndirect = new(false);
}
