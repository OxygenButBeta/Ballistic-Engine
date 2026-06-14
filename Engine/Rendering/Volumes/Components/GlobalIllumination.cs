namespace BallisticEngine;

// Global Illumination volume override. The realtime GI stack — auto-fit irradiance probes
// (diffuse base), auto-fit reflection probes (specular), and the SDF-GI off-screen bounce — all
// run by DEFAULT with zero setup. This component does NOT switch them on; it lets a scene TWEAK
// their strength (and optionally force the still-env-gated SDF-GI bounce on without the env var).
//
// Every parameter defaults to "current behaviour", so a scene with this override but nothing
// changed renders identically to a scene without it (the volume-framework contract: stack defaults
// mirror PostProcessSettings defaults). Add it, flip a dial, the auto-GI keeps working in realtime.
public sealed class GlobalIllumination : VolumeComponent {
    [Tooltip("Strength of the baked diffuse light-probe ambient (the auto-fit IrradianceVolume). " +
             "1 = physical. Lower fades toward the flat sky IBL; higher over-drives the probe bounce.")]
    public readonly ClampedFloatParameter probeIntensity = new(1f, 0f, 4f);

    [Tooltip("Strength of the baked local reflections (the auto-fit ReflectionVolume), multiplied " +
             "onto the volume's own Intensity. 1 = physical; lower fades toward the global skybox reflection.")]
    public readonly ClampedFloatParameter reflectionIntensity = new(1f, 0f, 4f);

    [Tooltip("Extra multiplier on the SDF-GI off-screen dynamic bounce (on top of its own default " +
             "and the probe<->SDF blend). 0 = no SDF bounce; 1 = default; >1 punches the dynamic GI.")]
    public readonly ClampedFloatParameter sdfIntensity = new(1f, 0f, 4f);

    [Tooltip("Force the SDF-GI off-screen bounce ON for this scene without the BALLISTIC_SDFGI env " +
             "var. SDF-GI stays env-gated by default while it matures; a scene that wants the dynamic " +
             "off-screen bounce just adds this override and ticks this.")]
    public readonly BoolParameter sdfForceEnabled = new(false);

    [Tooltip("Ambient shadow-fill: a tiny fraction of surface albedo (AO-modulated) added so enclosed " +
             "interiors never crush the shadowed side of geometry to pure black. 0.03 = subtle default; " +
             "raise for flatter/brighter ambient, 0 for physically-pure (deep blacks).")]
    public readonly ClampedFloatParameter ambientFloor = new(0.03f, 0f, 0.5f);

    // ---- Probe-grid density (the auto-fit IrradianceVolume / ReflectionVolume are AUTOMATIC; this
    // scales how finely they're sampled without placing/baking a component by hand) ----

    [Tooltip("Light-probe (diffuse GI) grid density multiplier. 1 = default auto-fit resolution. " +
             "Higher = more probes (sharper indirect light, slower bake); lower = coarser (faster).")]
    public readonly ClampedFloatParameter probeDensity = new(1f, 0.25f, 3f);

    [Tooltip("Reflection-probe (specular) grid density multiplier. 1 = default. Reflection cells are " +
             "expensive (a prefiltered cubemap each), so raise this sparingly.")]
    public readonly ClampedFloatParameter reflectionDensity = new(1f, 0.25f, 3f);

    // ---- Debug visualisation (gizmo overlays — the same toggles as the Scene-view toolbar, exposed
    // here so a scene/volume can pin them on) ----

    [Tooltip("DEBUG: draw the light-probe grid in the Scene view — GREEN = occupied (near geometry), " +
             "RED = empty air. Shows where probes are placed and how many fall in wasted empty space.")]
    public readonly BoolParameter debugShowProbes = new(false);

    [Tooltip("DEBUG: draw the reflection-probe cells in the Scene view (occupied cubemap cells vs " +
             "skybox-fallback cells), so you can see the specular grid coverage.")]
    public readonly BoolParameter debugShowReflectionProbes = new(false);
}
