namespace BallisticEngine;

// Cascaded sun-shadow settings as a volume override (HDRP-style "Shadows"): max distance,
// cascade count and split shape blend per volume, so an interior can pull the budget close
// (short distance, near-loaded splits) while a vista volume pushes it out. Per-light acne
// bias stays on DirectionalLight; this component owns the cascade LAYOUT.
public sealed class Shadows : VolumeComponent {
    [Tooltip("How far from the camera sun shadows reach, in world units. The cascades " +
             "subdivide this distance, so shorter = sharper shadows everywhere.")]
    public readonly ClampedFloatParameter maxDistance = new(60f, 5f, 500f);

    [Tooltip("Number of shadow cascades. More cascades = better texel density distribution, " +
             "one extra scene depth pass each.")]
    public readonly ClampedIntParameter cascadeCount = new(4, 1, 4);

    [Tooltip("Cascade split shape: 0 = uniform (favors distant detail), 1 = logarithmic " +
             "(favors close-up detail).")]
    public readonly ClampedFloatParameter splitDistribution = new(0.7f, 0f, 1f);

    [Tooltip("Cross-fade width at each cascade's edge (fraction of the cascade), hiding the " +
             "resolution step between cascades.")]
    public readonly ClampedFloatParameter cascadeBlend = new(0.15f, 0f, 0.5f);

    [Tooltip("Per-cascade shadow map resolution. Snapped to powers of two; higher = sharper " +
             "and slower.")]
    public readonly ClampedIntParameter resolution = new(2048, 512, 4096);

    [Tooltip("Shadow filtering: 0 = Hard (one tap), 1 = Soft PCF (fixed 5x5 blur), " +
             "2 = PCSS (contact-hardening: razor sharp at contact, softer with distance, " +
             "penumbra from the sun's Angular Diameter).")]
    public readonly ClampedIntParameter filtering = new(1, 0, 2);

    [Tooltip("PCSS penumbra scale. 1 = physical (real sun shadows are sharp); higher reads " +
             "as a hazier, larger light source.")]
    public readonly ClampedFloatParameter softness = new(2f, 0.25f, 8f);

    [Tooltip("Contact shadows: a short screen-space depth march toward the sun that grounds " +
             "small props and fine geometry the cascades miss. Refines, never lifts shadow.")]
    public readonly BoolParameter contactShadows = new(false);

    [Tooltip("World-space distance the contact-shadow ray marches (metres).")]
    public readonly ClampedFloatParameter contactLength = new(0.3f, 0.05f, 2f);

    [Tooltip("Contact-shadow march samples (cost vs. accuracy).")]
    public readonly ClampedIntParameter contactSteps = new(12, 4, 32);

    [Tooltip("Depth difference (metres) that counts as a contact-shadow hit.")]
    public readonly ClampedFloatParameter contactThickness = new(0.5f, 0.05f, 2f);
}
