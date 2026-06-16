namespace BallisticEngine;

// THE unified Global Illumination volume — the single override for INDIRECT light (diffuse GI +
// reflections), Lumen-style. It consolidates what used to be three+ separate volumes (the old
// ScreenSpaceGlobalIllumination, ScreenSpaceReflections, and the dead GL probe/Lumen overrides) into
// one clean front door:
//
//   GI Mode          Off | Screen-Space | Ray-Traced (Lumen)   — the diffuse indirect bounce technique
//   Reflections Mode Off | Screen-Space | Ray-Traced           — the specular indirect technique
//   + intensities, with the fiddly bounce/temporal/denoise dials tucked into an Advanced foldout.
//
// DOCTRINE: no front-door knob sprawl (the APV anti-pattern). The two mode dropdowns + two intensities
// are all most scenes touch; everything else has a good default and lives under Advanced. DIRECT light
// (sun/sky/exposure/shadows) is NOT here — it stays in its own components; this volume is indirect-only.
//
// Defaults mirror the engine PostProcessSettings defaults, so a scene that adds this override but
// changes nothing renders byte-identically to a scene without it (the volume-framework contract).
//
// MIGRATION: profiles authored against the old "ScreenSpaceGlobalIllumination" / "ScreenSpaceReflections"
// type names are remapped to this type on load (VolumeProfileLoader.LegacyTypeNames); matching parameter
// names carry their values over, so existing .volume assets keep their GI/reflection settings.
public sealed class GlobalIllumination : VolumeComponent {
    // ---- Master switch ----
    [Tooltip("Master enable for the ENTIRE indirect-lighting (Lumen) system. Off = a hard stop: NO diffuse " +
             "GI (SSGI/RT-GI), NO reflections (SSR/RT), NO DDGI world cache, NO screen probes, NO emissive-as-GI. " +
             "The scene falls back to the IBL ambient + skybox reflection only. Default ON (byte-identical to a " +
             "scene with no override). This is THE way to fully turn Lumen off from the volume.")]
    public readonly BoolParameter enabled = new(true);

    // ---- Diffuse GI (the indirect one-bounce light) ----
    [Tooltip("Diffuse global illumination technique. Off = IBL ambient only; Screen-Space = SSGI " +
             "(fast, screen-bounded one-bounce); Ray-Traced = DXR off-screen-aware one-bounce (Lumen), " +
             "falls back to Screen-Space on GPUs without ray tracing. All denoised with OIDN.")]
    public readonly EnumParameter<GiMode> giMode = new(GiMode.ScreenSpace);

    [Tooltip("Strength of the indirect diffuse bounce added over the IBL ambient base.")]
    public readonly ClampedFloatParameter intensity = new(1f, 0f, 4f);

    [Tooltip("Emissive surfaces act as area lights in the indirect bounce — a glowing sign or lava " +
             "spills coloured light onto nearby walls, not just onto the camera pixel. On by default " +
             "(the Lumen/RTXGI behaviour). No effect when GI Mode is Off.")]
    [HideIf("giMode", GiMode.Off)]
    public readonly BoolParameter emissiveAsGi = new(true);

    // ---- Specular GI (reflections) ----
    [Tooltip("Reflections technique. Off = IBL/skybox reflection only; Screen-Space = SSR (fast, " +
             "screen-bounded); Ray-Traced = DXR (off-screen geometry + sky reflect correctly), falls " +
             "back to Screen-Space without ray tracing.")]
    public readonly EnumParameter<ReflectionMode> reflectionsMode = new(ReflectionMode.ScreenSpace);

    [Tooltip("Strength of reflections on smooth surfaces.")]
    public readonly ClampedFloatParameter reflectionsIntensity = new(1f, 0f, 2f);

    // ---- Ray-Traced quality (only relevant when GI Mode = Ray-Traced) ----
    [Tooltip("DDGI world-probe radiance cache: a grid of probes that caches off-screen bounce light so " +
             "the Ray-Traced GI gathers MULTI-bounce far-field light (light from rooms/geometry the camera " +
             "can't see). Off = single-bounce screen-aware RT-GI only. Lumen's world radiance cache. " +
             "Ray-Traced GI only.")]
    [ShowIf("giMode", GiMode.RayTraced)]
    public readonly BoolParameter worldRadianceCache = new(false);

    [Tooltip("Screen-space radiance probes: the near/mid-field final-gather (Place->Trace->Blend->Integrate) " +
             "that hands far-field ray-misses to the DDGI world cache — Lumen's screen-trace -> world-cache " +
             "hierarchy. Off = the per-pixel DDGI gather fallback. Needs the World Radiance Cache on. " +
             "Ray-Traced GI only.")]
    [ShowIf("giMode", GiMode.RayTraced)]
    public readonly BoolParameter screenProbes = new(true);

    // ---- Debug ----
    [Tooltip("GI-isolate view: show ONLY the indirect bounce this GI pass adds (not the lit scene). " +
             "Black = no bounce here. The way to verify + tune GI — judge it by the isolated bounce.")]
    public readonly BoolParameter giIsolate = new(false);

    // ================= ADVANCED (good defaults; most scenes never touch these) =================
    [Header("Advanced — Diffuse Bounce")]
    [FoldoutGroup("Advanced")]
    [Tooltip("Max gather distance in metres (near vs far bounce reach).")]
    public readonly ClampedFloatParameter rayLength = new(12f, 1f, 40f);

    [FoldoutGroup("Advanced")]
    [Tooltip("Distance falloff exponent. 0 = no falloff; higher keeps bounce local.")]
    public readonly ClampedFloatParameter falloff = new(0.5f, 0f, 4f);

    [FoldoutGroup("Advanced")]
    [Tooltip("Assumed occluder thickness in metres. Thin lets light leak past railings/foliage; " +
             "thick treats them as walls.")]
    public readonly ClampedFloatParameter thickness = new(0.5f, 0.05f, 2f);

    [FoldoutGroup("Advanced")]
    [Tooltip("Boosts the bounce of brighter source pixels (a soft super-linear gain on radiant surfaces). " +
             "0 = physically neutral. Screen-Space mode.")]
    public readonly ClampedFloatParameter bounceBoost = new(0f, 0f, 4f);

    [FoldoutGroup("Advanced")]
    [Tooltip("How hard AO bites the bounce.")]
    public readonly ClampedFloatParameter occlusionPower = new(0.6f, 0f, 2f);

    [FoldoutGroup("Advanced")]
    [Tooltip("Horizon slices per pixel (bitmask gather). Temporal + denoise keep even 2–4 clean; " +
             ">8 is clamped. (Screen-Space mode only.)")]
    public readonly ClampedIntParameter rayCount = new(4, 1, 16);

    [Header("Advanced — Temporal")]
    [FoldoutGroup("Advanced")]
    [Tooltip("Temporal frames to accumulate. Higher = smoother but laggier.")]
    public readonly ClampedFloatParameter maxHistory = new(24f, 1f, 64f);

    [Header("Advanced — Look")]
    [FoldoutGroup("Advanced")]
    [Tooltip("Cinematic look strength on the local bounce (saturation + warmth).")]
    public readonly ClampedFloatParameter look = new(0.6f, 0f, 1f);

    [FoldoutGroup("Advanced")]
    [Tooltip("Tint multiplier on the bounce.")]
    public readonly ColorParameter tint = new(Vector3.One);

    [FoldoutGroup("Advanced")]
    [Tooltip("Bounce colour punch.")]
    public readonly ClampedFloatParameter saturation = new(1f, 0f, 2f);
}
