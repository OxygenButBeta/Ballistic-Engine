namespace BallisticEngine;

// Lumen = the engine's UE5-style global-illumination solution (mesh-card surface cache → SW/HW trace → screen-probe
// gather → world-space radiance cache → reflections), the DEFAULT GI. This is the artist-facing volume (mirrors
// AuroraVolume): scene-driven dials for intensity / sky / quality, blended by the VolumeManager like every other
// post component. Requires hardware ray tracing; without it GI is unavailable regardless of this toggle.
//
// Precedence with the env doors: the BALLISTIC_DX12_LUMEN_* env vars still OVERRIDE these (a set env var wins), so
// headless A/B and determinism runs are unaffected. With no env set, these volume values drive the renderer. The
// master GI selector (BALLISTIC_DX12_GI = lumen|aurora|off) still chooses WHICH GI system runs; this only tunes Lumen.
public sealed class LumenVolume : VolumeComponent {
    [Tooltip("Master switch for Lumen global illumination. Off → direct lighting + IBL + AO + shadows only. " +
             "Requires hardware ray tracing; without it GI is unavailable regardless of this toggle.")]
    public readonly BoolParameter enabled = new(true);

    [Tooltip("Quality preset. High = 12 screen rays / 6 radiosity rays / denoise on; Balanced = 8 / 4 (default); " +
             "Performance = 4 / 2. Custom = honour the explicit dials below.")]
    public readonly EnumParameter<LumenQuality> quality = new(LumenQuality.Balanced);

    [Tooltip("Overall strength of the diffuse GI contribution added to the scene (the screen-probe combine gain).")]
    public readonly ClampedFloatParameter intensity = new(1f, 0f, 8f);

    [Tooltip("How much skylight enters on a trace ray that misses all geometry (a sealed interior stays dark).")]
    public readonly ClampedFloatParameter skyIntensity = new(1f, 0f, 4f);

    [Tooltip("Indirect (radiosity / multi-bounce) hemisphere rays per surface-cache texel. Higher = richer bounce, " +
             "more lighting cost. Only honoured when Quality = Custom; presets set it.")]
    [ShowIf(nameof(quality), LumenQuality.Custom)]
    public readonly ClampedIntParameter indirectRays = new(4, 0, 8);

    [Tooltip("Surface-cache pages relit per frame = PageCount / this (UE-style amortization). Higher = cheaper but " +
             "slower light response; small scenes ignore it (relight fully). Only honoured when Quality = Custom.")]
    [ShowIf(nameof(quality), LumenQuality.Custom)]
    public readonly ClampedIntParameter lightingUpdateFactor = new(16, 1, 64);

    [Tooltip("Multiplier on the surface-cache lighting resolution (card page sizes). >1 sharper GI, more atlas " +
             "pressure (more dropped cards); <1 coarser but more coverage. Only honoured when Quality = Custom.")]
    [ShowIf(nameof(quality), LumenQuality.Custom)]
    public readonly ClampedFloatParameter resolutionScale = new(1f, 0.25f, 4f);

    [Tooltip("Lumen reflections — reflective surfaces mirror the lit surface cache (GI color bleed in reflections). " +
             "Off → the legacy SSR/RT reflections pass runs instead.")]
    public readonly BoolParameter reflections = new(true);

    [Tooltip("Volumetric GI — fog in-scatters the Lumen world-space radiance cache instead of a flat ambient, so " +
             "fog under an arch goes dark and fog by a coloured wall tints. No effect unless volumetric fog is on.")]
    public readonly BoolParameter volumetricGi = new(true);

    [Tooltip("DEBUG: replace the scene with the raw indirect irradiance E the GI gathers per pixel (before albedo/AO).")]
    public readonly BoolParameter debugRawIndirect = new(false);
}

// Lumen quality presets (mirror AuroraQuality). Custom honours the explicit dials.
public enum LumenQuality { Performance, Balanced, High, Custom }
