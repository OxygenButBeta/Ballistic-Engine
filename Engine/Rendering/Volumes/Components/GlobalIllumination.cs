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

    // ---- Quality preset ----
    [Tooltip("GI quality preset. Together with GI Mode this is THE control for GI — High (RTX 2060 ship " +
             "target) and Epic (RTX 3070+, more slices / longer temporal history / denser probes) are the " +
             "two front-door knobs; everything else has a good default under Advanced. High is byte-identical " +
             "to the engine defaults. The preset DRIVES the Advanced dials' effective values (slices/history); " +
             "Low (a No-RT survival floor) is deferred — the min target is RT-capable.")]
    [HideIf("enabled", false)]
    public readonly EnumParameter<GiQuality> giQuality = new(GiQuality.High);

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

    // ---- Baked (frozen) GI ----
    [Tooltip("BAKE the world-probe GI ONCE then FREEZE it: compute the indirect light progressively over the " +
             "first frames (the region around the camera first, the rest filling in over time — playable " +
             "immediately) and then stop updating it. Frozen = 0 rays/frame at runtime (near-free) and NO temporal " +
             "feedback, so GHOSTING is structurally impossible — the fix for 'performanssız + ghosting'. The " +
             "trade: a frozen field doesn't follow a moving sun (it auto re-bakes when the sun or the camera " +
             "moves far enough). Needs the World Radiance Cache. Ray-Traced GI only.")]
    [ShowIf("giMode", GiMode.RayTraced)]
    public readonly BoolParameter bakedGi = new(false);

    [Tooltip("Probe cascades: 1 = a single dense grid; 2 = a NEAR dense cascade (high detail) plus a FAR sparse " +
             "cascade (wide coverage, no GI falloff at the grid edge). Baked GI only — the frozen field pays the " +
             "cost once, so the extra cascade is free at runtime. Ray-Traced GI only.")]
    [ShowIf("bakedGi", true)]
    public readonly ClampedIntParameter cascades = new(2, 1, 2);

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

    // CHUNK6: the three temporal dials are MEANINGLESS once GI is BAKED (a frozen field has no per-frame temporal
    // accumulation — that's the whole point: 0 ghosting by construction). Hidden when bakedGi is on so the
    // inspector doesn't show inert knobs ("kullanılamayan şeyler çöpe"). They stay live (and visible) for the
    // non-baked DDGI/SSGI path. Hidden, not deleted — toggling bakedGi off restores them with their values.
    [Header("Advanced — Temporal")]
    [FoldoutGroup("Advanced")]
    [HideIf("bakedGi", true)]
    [Tooltip("Temporal frames to accumulate. Higher = smoother but laggier. (Live GI only — baked GI is frozen.)")]
    public readonly ClampedFloatParameter maxHistory = new(24f, 1f, 64f);

    [FoldoutGroup("Advanced")]
    [HideIf("bakedGi", true)]
    [Tooltip("Ghosting reject: how aggressively a moving camera flushes the temporal trail. 0 = never flush " +
             "(maximum smoothing, but a fast pan smears/ghosts); higher = a pan collapses the accumulation faster " +
             "(kills ghosting, but more per-frame grain shows while moving). (Live GI only — baked GI has no " +
             "temporal trail, so ghosting is impossible by construction.)")]
    public readonly ClampedFloatParameter ghostingReject = new(0.06f, 0f, 0.5f);

    [FoldoutGroup("Advanced")]
    [HideIf("bakedGi", true)]
    [Tooltip("Temporal clamp tightness: how far accumulated history may stray from the current local bounce " +
             "before it's clamped. (Live GI only — baked GI is frozen, no history to clamp.)")]
    public readonly ClampedFloatParameter temporalClamp = new(1.6f, 1f, 4f);

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
