namespace BallisticEngine;

// Lumen (SDF-GI) volume override — the off-screen DYNAMIC global-illumination bounce: mesh-SDF world
// tracing that lights surfaces from geometry the screen can't see (the thing SSGI/probes can't do).
// Split out of the old GlobalIllumination so a scene can turn Lumen OFF independently (it's the
// heaviest GI pass, and a user may not want dynamic bounce at all) or punch it up.
//
// Lumen stays gated by default (BALLISTIC_SDFGI env) while it matures; `enabled` here turns it on for
// a scene without the env var. Defaults render identically to no-override.
public sealed class Lumen : VolumeComponent {
    [Tooltip("Master switch for the Lumen SDF-GI off-screen dynamic bounce. On = enable it for this " +
             "scene without the BALLISTIC_SDFGI env var; Off = no SDF bounce (probes + SSGI still run). " +
             "This is the 'stop Lumen' switch — the dynamic bounce is the most expensive GI pass.")]
    public readonly BoolParameter enabled = new(false);

    [Tooltip("Strength multiplier on the SDF-GI off-screen bounce (on top of its default and the " +
             "probe<->SDF blend). 0 = no bounce; 1 = default; >1 punches the dynamic GI.")]
    public readonly ClampedFloatParameter intensity = new(1f, 0f, 4f);
}
