
namespace BallisticEngine;

public class ClampedFloatParameter : FloatParameter {
    public float Min { get; }
    public float Max { get; }

    public ClampedFloatParameter(float value, float min, float max, bool overridden = false)
        : base(value, overridden) {
        Min = min;
        Max = max;
        this.value = Math.Clamp(value, min, max);
    }

    public override float Value {
        get => value;
        set => this.value = Math.Clamp(value, Min, Max);
    }
}
