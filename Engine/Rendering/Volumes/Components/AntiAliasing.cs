namespace BallisticEngine;

public sealed class AntiAliasing : VolumeComponent {
    [Tooltip("Temporal AA: jittered render + history. Enables SSR/SSGI/volumetrics (they need MSAA off).")]
    public readonly BoolParameter taaEnabled = new(true);

    [Tooltip("History weight. Higher = smoother but more ghosting on motion.")]
    public readonly ClampedFloatParameter taaFeedback = new(0.9f, 0.5f, 0.98f);

    [Tooltip("MSAA samples. Ignored while TAA is on (TAA forces MSAA off).")]
    public readonly ClampedIntParameter msaaSamples = new(4, 1, 8);
}
