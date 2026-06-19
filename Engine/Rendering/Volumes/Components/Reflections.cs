namespace BallisticEngine;

// Reflections override — the single product control for realtime specular reflections. One dropdown picks the
// technique: Off (specular comes from the IBL/skybox cube only), Screen Space (SSR — fast, screen-bounded), or
// Ray Traced (DXR — off-screen + sky reflect correctly, falls back to SSR without hardware RT). The mode IS the
// master gate: Off skips the reflections pass entirely (drives PostFX.SsrEnabled = mode != Off in the bridge).
//
// Defaults mirror PostProcessSettings (mode Off, intensity 1, cache-fed RT on) so a scene with no Reflection
// volume behaves byte-identically to the engine defaults. Drives the DX12 reflections pass via
// VolumePostProcessing.Apply → PostProcessSettings.{ReflectionMode,SsrEnabled,SsrIntensity,LumenReflections}.
public sealed class Reflections : VolumeComponent {
    [Tooltip("Reflection technique. Off = IBL/skybox cube only. Screen Space = SSR (fast, screen-bounded). " +
             "Ray Traced = DXR (off-screen + sky reflect correctly; falls back to SSR without hardware RT).")]
    public readonly EnumParameter<ReflectionMode> mode = new(ReflectionMode.Off);

    [Tooltip("Overall strength of the reflection contribution. 1 = physical. Applies to SSR and RT.")]
    [HideIf("mode", ReflectionMode.Off)]
    public readonly ClampedFloatParameter intensity = new(1f, 0f, 4f);

    [Tooltip("Feed ray-traced reflections from the Lumen surface-card radiance cache (reflections see GI, " +
             "rough + sharp), with IBL only as the miss/far fallback. Requires Lumen GI to be active.")]
    [ShowIf("mode", ReflectionMode.RayTraced)]
    public readonly BoolParameter sampleRadianceCache = new(true);
}
