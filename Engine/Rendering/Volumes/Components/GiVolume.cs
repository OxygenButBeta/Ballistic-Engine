namespace BallisticEngine;

public enum GiQuality { High, Balanced, Performance, Custom }

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
