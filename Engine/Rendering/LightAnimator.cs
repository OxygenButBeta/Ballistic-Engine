
namespace BallisticEngine;

[Component("Light Animator", "Rendering")]
public sealed class LightAnimator : Behaviour {
    public enum Mode {
        Flicker,
        Pulse,
        Curve,
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

    void Capture() {
        if (captured) return;
        if (point is not null) { baseIntensity = point.Intensity; baseColor = point.Color; captured = true; }
        else if (spot is not null) { baseIntensity = spot.Intensity; baseColor = spot.Color; captured = true; }
    }

    void Restore() {
        if (!captured) return;
        if (point is not null) { point.Intensity = baseIntensity; point.Color = baseColor; }
        else if (spot is not null) { spot.Intensity = baseIntensity; spot.Color = baseColor; }
        captured = false;
    }

    public void RestoreBase() => Restore();

    protected internal override void Tick(in float delta) {
        clock += delta;
        Apply(clock);
    }

    public void Apply(float t) {
        if (point is null && spot is null) Resolve();
        Capture();
        if (!captured) return;

        float period = MathF.Max(Period, 0.0001f);
        float phase = (t / period) % 1f;
        if (phase < 0f) phase += 1f;

        float factor;
        switch (Animation) {
            case Mode.Pulse:
                factor = 0.5f + 0.5f * MathF.Sin(phase * MathF.Tau - MathF.PI * 0.5f);
                break;
            case Mode.Curve:
                factor = IntensityCurve is { Count: > 0 } ? Math.Clamp(IntensityCurve.Evaluate(phase), 0f, 1f) : 1f;
                break;
            default:
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

    static float FlickerNoise(float x) {
        float n = ValueNoise(x) * 0.65f + ValueNoise(x * 2.37f + 11.3f) * 0.35f;
        return Math.Clamp(n, 0f, 1f);
    }

    static float ValueNoise(float x) {
        int i = (int)MathF.Floor(x);
        float f = x - i;
        float a = Hash01(i), b = Hash01(i + 1);
        float u = f * f * (3f - 2f * f);
        return a + (b - a) * u;
    }

    static float Hash01(int n) {
        uint h = (uint)n * 2654435761u;
        h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
        return (h & 0xFFFFFF) / (float)0x1000000;
    }
}
