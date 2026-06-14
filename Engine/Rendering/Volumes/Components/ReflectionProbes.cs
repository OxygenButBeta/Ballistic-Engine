namespace BallisticEngine;

// Reflection Probes (specular GI) volume override. The auto-fit ReflectionVolume runs by DEFAULT
// (sparse local cubemaps, realtime); this override tweaks or DISABLES it independently of the diffuse
// light probes and the Lumen SDF bounce. Off = fall back to the global skybox reflection everywhere.
//
// Defaults mirror the engine defaults (identical render when added unchanged).
public sealed class ReflectionProbes : VolumeComponent {
    [Tooltip("Master switch for the baked local reflections. Off = every surface reflects the global " +
             "skybox IBL only (no room-local cubemaps). Disable without touching diffuse probes or Lumen.")]
    public readonly BoolParameter enabled = new(true);

    [Tooltip("Strength of the baked local reflections (multiplied onto the volume's own Intensity). " +
             "1 = physical; lower fades toward the global skybox reflection.")]
    public readonly ClampedFloatParameter intensity = new(1f, 0f, 4f);

    [Tooltip("Reflection-probe grid density multiplier. 1 = default. Reflection cells are expensive (a " +
             "prefiltered cubemap + VRAM each), so raise this sparingly.")]
    public readonly ClampedFloatParameter density = new(1f, 0.25f, 3f);

    [Tooltip("DEBUG: draw the reflection-probe cells in the Scene view — CYAN = a captured local " +
             "cubemap cell, dim BLUE = empty cell that falls back to the skybox. Shows specular coverage.")]
    public readonly BoolParameter debugShow = new(false);
}
