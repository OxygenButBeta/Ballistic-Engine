namespace BallisticEngine;

// Volumetric Lighting: the one-stop atmospheric override that bundles three layers driven off a single
// camera-direction raymarch (no extra GPU pass — they share the fog march that already samples the sun
// cascades each step):
//
//   • Fog       — physical exponential height fog (extinction + sun/sky in-scatter). Same medium as the
//                 old VolumetricFog override (which this supersedes; that one is now hidden from the menu).
//   • God Rays  — an AESTHETIC light-shaft layer with its OWN visibility density, DECOUPLED from the fog
//                 density. The point: crisp sun shafts WITHOUT cranking the fog to non-physical values.
//   • Dust      — procedural sun-lit motes floating in the air (a 3D noise field along the same march),
//                 shadow-gated so they only sparkle where the sun reaches, drifting over time.
//
// Everything off by default — it's an atmospheric, scene-dependent look. Turning the override on with
// default values reproduces the old physical fog exactly; God Rays and Dust are independent opt-ins.
public sealed class VolumetricLighting : VolumeComponent {
    // --- Fog (physical) ---
    [Tooltip("Master toggle for the physical fog medium (the height fog + sun/sky in-scatter).")]
    public readonly BoolParameter enabled = new(false);

    [Tooltip("Master strength: fades the whole fog out below 1, boosts only the glow above 1.")]
    public readonly ClampedFloatParameter intensity = new(1f, 0f, 4f);

    [Tooltip("Fog extinction at the base height, 1/m. 0.002 ≈ 2 km visibility (light haze); 0.01 = mist; 0.05 = fog bank.")]
    public readonly ClampedFloatParameter density = new(0.002f, 0f, 0.2f);

    [Tooltip("How fast the fog thins with altitude, 1/m (0 = uniform). 0.04 ≈ a 25 m-thick ground layer.")]
    public readonly ClampedFloatParameter heightFalloff = new(0.04f, 0f, 0.5f);

    [Tooltip("World height below which the fog is at full density.")]
    public readonly FloatParameter baseHeight = new(0f);

    [Tooltip("Sunlight in-scatter multiplier (1 = physical balance against the sky).")]
    public readonly ClampedFloatParameter scattering = new(1f, 0f, 4f);

    [Tooltip("Skylight in-scatter multiplier (1 = physical balance). Drives the fog's ambient color from the baked sky.")]
    public readonly ClampedFloatParameter ambientScatter = new(1f, 0f, 2f);

    [Tooltip("Phase anisotropy of the FOG sun in-scatter (the god-ray layer has its own).")]
    public readonly ClampedFloatParameter anisotropy = new(0.7f, 0f, 0.95f);

    [Tooltip("Extra blaze around the sun disk seen through the fog.")]
    public readonly ClampedFloatParameter sunGlow = new(0.3f, 0f, 2f);

    [Tooltip("Tightness of the sun-disk glow (higher = smaller/hotter).")]
    public readonly ClampedFloatParameter sunGlowSharpness = new(48f, 1f, 128f);

    [Tooltip("Raymarch samples inside shadow-map range (cost vs banding). Shared by all three layers.")]
    public readonly ClampedIntParameter stepCount = new(48, 8, 128);

    [Tooltip("Metres the shadowed march reaches; beyond it the fog continues analytically (sun counted lit).")]
    public readonly ClampedFloatParameter maxDistance = new(120f, 10f, 400f);

    [Tooltip("Colour grade on the fog's in-scatter (extinction stays neutral).")]
    public readonly ColorParameter tint = new(Vector3.One);

    // --- God Rays (aesthetic shafts, independent of fog density) ---
    [FoldoutGroup("God Rays", defaultOpen: false)]
    [Tooltip("Turn on the aesthetic light-shaft layer. Works even with the fog density at a physical (low) value.")]
    public readonly BoolParameter shaftsEnabled = new(false);

    [FoldoutGroup("God Rays")]
    [Tooltip("Overall shaft brightness.")]
    public readonly ClampedFloatParameter shaftIntensity = new(1f, 0f, 8f);

    [FoldoutGroup("God Rays")]
    [Tooltip("Shaft visibility weight (1/m), DECOUPLED from the fog density — this is the dial to push for crisp shafts without thickening the fog.")]
    public readonly ClampedFloatParameter shaftDensity = new(0.05f, 0f, 0.5f);

    [FoldoutGroup("God Rays")]
    [Tooltip("Fade with march distance, 1/m. 0 = shafts reach the full march; higher = only near shafts glow.")]
    public readonly ClampedFloatParameter shaftDecay = new(0f, 0f, 0.1f);

    [FoldoutGroup("God Rays")]
    [Tooltip("Shaft phase anisotropy (higher = tighter, more defined rays toward the sun).")]
    public readonly ClampedFloatParameter shaftSharpness = new(0.85f, 0f, 0.97f);

    [FoldoutGroup("God Rays")]
    [Tooltip("Colour grade on the shafts only.")]
    public readonly ColorParameter shaftTint = new(Vector3.One);

    // --- Dust (procedural floating motes) ---
    [FoldoutGroup("Dust", defaultOpen: false)]
    [Tooltip("Turn on procedural sun-lit dust motes floating in the air (no scene objects; a noise field along the march).")]
    public readonly BoolParameter dustEnabled = new(false);

    [FoldoutGroup("Dust")]
    [Tooltip("Overall dust glow strength.")]
    public readonly ClampedFloatParameter dustIntensity = new(0.5f, 0f, 4f);

    [FoldoutGroup("Dust")]
    [Tooltip("Mote density/size: lower = larger, sparser motes; higher = fine, dense dust.")]
    public readonly ClampedFloatParameter dustSize = new(0.5f, 0.05f, 4f);

    [FoldoutGroup("Dust")]
    [Tooltip("World-space drift velocity (m/s) of the dust field — a gentle air current. Animated; frozen under deterministic capture.")]
    public readonly Vector3Parameter dustDrift = new(new Vector3(0.15f, 0.08f, 0.05f));

    [FoldoutGroup("Dust")]
    [Tooltip("How strongly the motes catch the sun (twinkle gain).")]
    public readonly ClampedFloatParameter dustSparkle = new(1f, 0f, 4f);
}
