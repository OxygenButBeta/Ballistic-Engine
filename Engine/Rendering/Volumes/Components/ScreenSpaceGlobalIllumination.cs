using OpenTK.Mathematics;

namespace BallisticEngine;

// Screen-space GI: a bounded one-bounce refinement on the physical IBL base.
public sealed class ScreenSpaceGlobalIllumination : VolumeComponent {
    public readonly BoolParameter enabled = new(true);

    [Tooltip("Cinematic look strength on the local bounce (saturation + warmth).")]
    public readonly ClampedFloatParameter look = new(0.6f, 0f, 1f);

    [Tooltip("Show only the bounce SSGI adds (10x brightened). Black = no gather. Use to verify/tune GI.")]
    public readonly BoolParameter debugView = new(false);

    [Tooltip("Sky light for rays that miss on-screen geometry. 0 = off (the IBL ambient already counts the sky; " +
             "non-zero double-counts it as a gray veil). Raise only in closed interiors with openings.")]
    public readonly ClampedFloatParameter skyFallback = new(0f, 0f, 1f);

    [Tooltip("Rays per pixel. Temporal + denoise keep even 2-4 clean.")]
    public readonly ClampedIntParameter rayCount = new(4, 1, 16);

    [Tooltip("Temporal frames to accumulate. Higher = smoother but laggier.")]
    public readonly ClampedFloatParameter maxHistory = new(24f, 1f, 64f);

    [Tooltip("Spatial denoiser tap spacing. Wider = smoother.")]
    public readonly ClampedFloatParameter denoise = new(2f, 1f, 8f);

    [Tooltip("Max gather distance in metres (near vs far bounce reach).")]
    public readonly ClampedFloatParameter rayLength = new(12f, 1f, 40f);

    [Tooltip("Distance falloff exponent. 0 = no falloff; higher keeps bounce local.")]
    public readonly ClampedFloatParameter falloff = new(0.5f, 0f, 4f);

    [Tooltip("Depth-test tolerance during the march. Thin = strict, thick = forgiving.")]
    public readonly ClampedFloatParameter thickness = new(0.5f, 0.05f, 2f);

    [Tooltip("Strength of the local one-bounce colour added over the IBL.")]
    public readonly ClampedFloatParameter intensity = new(1f, 0f, 4f);

    [Tooltip("Tint multiplier on the bounce.")]
    public readonly ColorParameter tint = new(Vector3.One);

    [Tooltip("Bounce colour punch.")]
    public readonly ClampedFloatParameter saturation = new(1f, 0f, 2f);

    [Tooltip("How hard AO bites the bounce.")]
    public readonly ClampedFloatParameter occlusionPower = new(0.6f, 0f, 2f);

    [Tooltip("Fraction of last frame's GI that re-bounces (fake multi-bounce).")]
    public readonly ClampedFloatParameter multiBounce = new(0.5f, 0f, 1f);
}
