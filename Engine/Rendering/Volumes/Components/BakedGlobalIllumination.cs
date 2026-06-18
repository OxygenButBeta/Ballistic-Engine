namespace BallisticEngine;

// BAKED GLOBAL ILLUMINATION — the dedicated, debuggable front door for the bake-once-then-freeze GI
// (2026-06-18, user: "ayrı volume aç, bake yöntemimizle ilgili her şey orada olsun, açıp kapatabileyim,
// debug edebileyim"). EVERYTHING about the baked DDGI lives here, in one place:
//
//   • enable           — the master on/off (debug toggle: see exactly what the baked GI adds)
//   • rebake           — a one-shot "Rebake now" trigger (re-runs the progressive bake near-first)
//   • quality          — probe density (cascades), rays/probe, octahedral resolution, converge depth
//   • progressive      — how the bake ripples out from the camera (band width / pacing)
//   • debug            — GI-isolate, probe-grid gizmo, probe spheres, draw distance, bake-progress %
//
// This is SEPARATE from the legacy GlobalIllumination volume (which drove the now-removed realtime GI). The
// baked GI is reached ONLY through this volume — and ONLY when the renderer's master GI gate is opted-in
// (BALLISTIC_DX12_GI_FORCE=1), so the DXR bake never runs unless explicitly enabled (it crashed the PC as an
// always-on default). Defaults here mirror the safe baked config (warmup capture-only, single dense cascade).
//
// How it works (the system this volume drives): a camera-centered 3D grid of DXR probes is traced + blended
// ONCE (progressively — the region around the camera first, the rest amortized over frames so it never blocks
// the GPU / trips the TDR watchdog), then FROZEN: 0 rays/frame at runtime, no temporal feedback → no ghosting.
// A frozen field doesn't follow a moving sun, so it auto-rebakes when the sun or the camera moves far enough.
public sealed class BakedGlobalIllumination : VolumeComponent {
    // ---- Master ----
    [Tooltip("Master enable for the baked GI. Off = no indirect bounce (IBL ambient only) — flip it to see " +
             "EXACTLY what the bake adds (the debug on/off). Note: the renderer's global GI gate must also be " +
             "opted-in (BALLISTIC_DX12_GI_FORCE=1) for the DXR bake to run at all — the safety lock after the crash.")]
    public readonly BoolParameter enabled = new(true);

    [Tooltip("Strength of the baked indirect bounce added over the IBL ambient base.")]
    [ShowIf("enabled", true)]
    public readonly ClampedFloatParameter intensity = new(1f, 0f, 4f);

    [Tooltip("Emissive surfaces act as area lights in the bake — a glowing sign spills coloured light onto " +
             "nearby walls. Baked once like the rest.")]
    [ShowIf("enabled", true)]
    public readonly BoolParameter emissiveAsGi = new(true);

    // ---- Rebake ----
    [Tooltip("Tick to RE-BAKE now: re-runs the progressive bake from scratch (camera region first). Use after " +
             "moving geometry / changing lights. Auto-resets to off once the renderer consumes it. (The bake also " +
             "auto-rebakes on a large camera move or a sun/light change.)")]
    [ShowIf("enabled", true)]
    public readonly BoolParameter rebakeNow = new(false);

    // ---- Quality ----
    [Tooltip("Probe cascades. 1 = a single dense grid (safe, validated). 2 = a NEAR dense cascade (detail) plus a " +
             "FAR sparse cascade (wide coverage). 2 is the prime GPU-hang suspect and is OFF by default until " +
             "re-validated — raise to 2 only deliberately.")]
    [ShowIf("enabled", true)]
    public readonly ClampedIntParameter cascades = new(1, 1, 2);

    [Tooltip("Rays traced per probe during the bake. Higher = cleaner, deeper indirect (the frozen field pays " +
             "this once, so it's free at runtime). 144 is the live-parity floor; 256 is the max-fidelity bake.")]
    [ShowIf("enabled", true)]
    public readonly ClampedIntParameter raysPerProbe = new(256, 16, 256);

    [Tooltip("Probe spacing in metres (near cascade). Denser (smaller) = finer indirect detail, less trilinear " +
             "blur between probes, but a smaller covered volume. ~1.2 m suits an interior.")]
    [ShowIf("enabled", true)]
    public readonly ClampedFloatParameter probeSpacing = new(1.2f, 0.4f, 4f);

    // ================= ADVANCED (good defaults; most scenes never touch these) =================
    [Header("Advanced — Converge")]
    [FoldoutGroup("Advanced")]
    [Tooltip("How many times each probe traces before it freezes. Deeper = cleaner, slower to settle. The bake is " +
             "one-shot so this is a quality lever, not a per-frame cost.")]
    public readonly ClampedIntParameter convergeTarget = new(48, 4, 128);

    [Header("Advanced — Progressive")]
    [FoldoutGroup("Advanced")]
    [Tooltip("How fast the bake frontier ripples outward from the camera (frames before the next distance band " +
             "opens). Lower = the far field fills in faster but each frame does more GPU work; higher = gentler.")]
    public readonly ClampedIntParameter bandFrames = new(2, 1, 16);

    // ================= DEBUG =================
    [Header("Debug")]
    [Tooltip("GI-isolate: show ONLY the baked indirect bounce (black = no bounce here). The way to verify + tune " +
             "the bake — judge it by the isolated contribution.")]
    public readonly BoolParameter giIsolate = new(false);

    [Tooltip("Draw the probe grid as gizmos in the Scene view — see WHERE the probes are and (with spheres) what " +
             "colour each one cached. The honest answer to 'is it baking / what is it sampling'.")]
    public readonly BoolParameter showProbeGrid = new(false);

    [Tooltip("Draw each visible probe as a SOLID coloured sphere tinted with its real cached irradiance (vs a " +
             "faint cross marker). The bold dots make the baked field obvious at a glance.")]
    [ShowIf("showProbeGrid", true)]
    public readonly BoolParameter showProbeSpheres = new(true);

    [Tooltip("How far from the camera (metres) to draw probe gizmos. Keeps the grid readable — distant probes are culled.")]
    [ShowIf("showProbeGrid", true)]
    public readonly ClampedFloatParameter probeDrawDistance = new(12f, 2f, 40f);
}
