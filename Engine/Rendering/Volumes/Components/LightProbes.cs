namespace BallisticEngine;

// Light Probes (diffuse GI) volume override. The auto-fit IrradianceVolume runs by DEFAULT (zero
// setup, realtime, sky-primed); this override lets a scene TWEAK or DISABLE it independently of the
// reflection probes and the Lumen SDF bounce — split out of the old monolithic GlobalIllumination so
// you can, e.g., kill probe ambient while keeping reflections.
//
// Defaults mirror the engine defaults, so adding this override with nothing changed renders identically.
public sealed class LightProbes : VolumeComponent {
    [Tooltip("Master switch for the diffuse light-probe ambient. Off = no probe GI (falls back to the " +
             "flat sky IBL ambient). Lets you disable probes without touching reflections or Lumen.")]
    public readonly BoolParameter enabled = new(true);

    [Tooltip("Strength of the baked diffuse light-probe ambient. 1 = physical. Lower fades toward the " +
             "flat sky IBL; higher over-drives the probe bounce.")]
    public readonly ClampedFloatParameter intensity = new(1f, 0f, 4f);

    [Tooltip("Probe-grid density multiplier. 1 = default auto-fit resolution. Higher = more probes " +
             "(sharper indirect light, slower bake); lower = coarser/faster. Re-bakes in the background.")]
    public readonly ClampedFloatParameter density = new(1f, 0.25f, 3f);

    [Tooltip("DEBUG: draw the light-probe grid in the Scene view — GREEN = occupied (near geometry), " +
             "RED = empty air. Shows where probes are placed and how many fall in wasted empty space.")]
    public readonly BoolParameter debugShow = new(false);
}
