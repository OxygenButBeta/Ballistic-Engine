
namespace BallisticEngine;

public class ClampedIntParameter : IntParameter {
    public int Min { get; }
    public int Max { get; }

    public ClampedIntParameter(int value, int min, int max, bool overridden = false)
        : base(value, overridden) {
        Min = min;
        Max = max;
        this.value = Math.Clamp(value, min, max);
    }

    public override int Value {
        get => value;
        set => this.value = Math.Clamp(value, Min, Max);
    }
}
