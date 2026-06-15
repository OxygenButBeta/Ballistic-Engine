
namespace BallisticEngine;

// Animates a light's intensity (and optionally color) over time — torches/fire (Flicker), neon/alarm
// pulses (Pulse), or a fully authored loop (Curve). Drives whichever light component sits on the same
// entity (PointLight or SpotLight). The third adopter of the reusable [[AnimationCurve]] +
// [[ColorGradient]] primitives, demonstrating they compose anywhere with zero new wiring.
//
// The light's authored Intensity/Color are the BASE: the animator multiplies intensity by a 0..1-ish
// factor and (optionally) tints the color from a gradient. On stop / detach the base is restored, so
// toggling the animator never corrupts the saved light values (Unity-style). Self-clocked (accumulates
// its own time) so it's pausable and deterministic.
[Component("Light Animator", "Rendering")]
public sealed class LightAnimator : Behaviour {
    public enum Mode {
        Flicker, // smooth pseudo-random noise — fire, torches, faulty bulbs
        Pulse,   // sine wave — neon, alarms, breathing glow
        Curve,   // an authored AnimationCurve looped over Period
    }

    [Tooltip("How the intensity is animated over time.")]
    public Mode Animation { get; set; } = Mode.Flicker;

    [Tooltip("Seconds for one full cycle (Pulse/Curve period; Flicker base time scale).")]
    [Range(0.01f, 30f)]
    public float Period { get; set; } = 1f;

    [Tooltip("Lowest intensity multiplier on the light's base Intensity (0 = fully dark at the trough).")]
    [Range(0f, 1f)]
    public float MinIntensity { get; set; } = 0.6f;

    [Tooltip("Highest intensity multiplier on the light's base Intensity (1 = the light's authored value).")]
    [Range(0f, 4f)]
    public float MaxIntensity { get; set; } = 1f;

    [Tooltip("Flicker only: higher = busier, more erratic flicker.")]
    [Range(0.1f, 20f)]
    public float FlickerSpeed { get; set; } = 6f;

    [Tooltip("Curve only: intensity over one normalized Period (X 0..1, Y 0..1 -> mapped into Min..Max).")]
    public AnimationCurve IntensityCurve { get; set; } = new();

    [Tooltip("Optional color over one normalized Period (tints the light's base color). Empty = base color.")]
    public ColorGradient ColorOverTime { get; set; } = new();

    // The captured base values (the light's authored Intensity/Color), restored on stop.
    float baseIntensity;
    Vector3 baseColor;
    bool captured;

    PointLight point;
    SpotLight spot;
    float clock;

    protected internal override void OnBegin() {
        Resolve();
        Capture();
    }

    protected internal override void OnDisabled() => Restore();
    protected internal override void OnDetach() => Restore();

    void Resolve() {
        point = GetComponent<PointLight>();
        spot = point is null ? GetComponent<SpotLight>() : null;
    }

    // Grabs the light's authored values once, so the animation multiplies a stable base.
    void Capture() {
        if (captured) return;
        if (point is not null) { baseIntensity = point.Intensity; baseColor = point.Color; captured = true; }
        else if (spot is not null) { baseIntensity = spot.Intensity; baseColor = spot.Color; captured = true; }
    }

    // Puts the light back to its authored base (so disabling the animator doesn't strand a dimmed value).
    void Restore() {
        if (!captured) return;
        if (point is not null) { point.Intensity = baseIntensity; point.Color = baseColor; }
        else if (spot is not null) { spot.Intensity = baseIntensity; spot.Color = baseColor; }
        captured = false;
    }

    // Public for the editor preview: restore the light to its authored base when preview stops, so the
    // light isn't stranded at a dimmed/animated value in edit mode.
    public void RestoreBase() => Restore();

    protected internal override void Tick(in float delta) {
        clock += delta;
        Apply(clock);
    }

    // Computes + writes the animated intensity/color at absolute time `t`. Public so the editor can
    // drive a live preview in edit mode (same path as play-mode Tick).
    public void Apply(float t) {
        if (point is null && spot is null) Resolve();
        Capture();
        if (!captured) return;

        float period = MathF.Max(Period, 0.0001f);
        float phase = (t / period) % 1f;
        if (phase < 0f) phase += 1f;

        // 0..1 factor by mode.
        float factor;
        switch (Animation) {
            case Mode.Pulse:
                // Sine 0..1 (one full cycle per Period).
                factor = 0.5f + 0.5f * MathF.Sin(phase * MathF.Tau - MathF.PI * 0.5f);
                break;
            case Mode.Curve:
                factor = IntensityCurve is { Count: > 0 } ? Math.Clamp(IntensityCurve.Evaluate(phase), 0f, 1f) : 1f;
                break;
            default: // Flicker
                factor = FlickerNoise(t * FlickerSpeed / period);
                break;
        }

        float mul = MinIntensity + (MaxIntensity - MinIntensity) * factor;
        Vector3 tint = ColorOverTime is { IsEmpty: false }
            ? baseColor * ColorOverTime.EvaluateColor(phase)
            : baseColor;

        if (point is not null) { point.Intensity = baseIntensity * mul; point.Color = tint; }
        else if (spot is not null) { spot.Intensity = baseIntensity * mul; spot.Color = tint; }
    }

    // Smooth value-noise in [0,1] from interpolated hashed lattice points — deterministic (no RNG), so
    // flicker is reproducible and testable. Two octaves for a natural, non-uniform fire flicker.
    static float FlickerNoise(float x) {
        float n = ValueNoise(x) * 0.65f + ValueNoise(x * 2.37f + 11.3f) * 0.35f;
        return Math.Clamp(n, 0f, 1f);
    }

    static float ValueNoise(float x) {
        int i = (int)MathF.Floor(x);
        float f = x - i;
        float a = Hash01(i), b = Hash01(i + 1);
        float u = f * f * (3f - 2f * f); // smoothstep
        return a + (b - a) * u;
    }

    // Deterministic hash of an int -> [0,1).
    static float Hash01(int n) {
        uint h = (uint)n * 2654435761u;
        h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
        return (h & 0xFFFFFF) / (float)0x1000000;
    }
}
