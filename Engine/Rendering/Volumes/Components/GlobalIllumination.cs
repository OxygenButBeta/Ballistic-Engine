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
}
