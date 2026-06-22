
namespace BallisticEngine;

public abstract class VolumeParameter {
    public bool Overridden { get; set; }

    internal abstract void Interp(VolumeParameter to, float t);

    internal abstract void CopyValueFrom(VolumeParameter source);
}

public class VolumeParameter<T> : VolumeParameter {
    protected T value;

    public virtual T Value {
        get => value;
        set => this.value = value;
    }

    protected VolumeParameter(T value, bool overridden) {
        this.value = value;
        Overridden = overridden;
    }

    internal override void Interp(VolumeParameter to, float t) {
        if (t > 0f)
            value = ((VolumeParameter<T>)to).value;
    }

    internal override void CopyValueFrom(VolumeParameter source) =>
        value = ((VolumeParameter<T>)source).value;
}

public class BoolParameter(bool value, bool overridden = false) : VolumeParameter<bool>(value, overridden);

public class FloatParameter(float value, bool overridden = false) : VolumeParameter<float>(value, overridden)
{
    internal override void Interp(VolumeParameter to, float t) =>
        value += (((VolumeParameter<float>)to).Value - value) * t;
}

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

public interface IEnumParameter {
    string[] Names { get; }
    int Index { get; set; }
}

public class EnumParameter<T>(T value, bool overridden = false) : VolumeParameter<T>(value, overridden), IEnumParameter
    where T : struct, Enum
{
    static readonly T[] Values = Enum.GetValues<T>();
    static readonly string[] ValueNames = Enum.GetNames<T>();

    public string[] Names => ValueNames;

    public int Index {
        get => Array.IndexOf(Values, value);
        set => this.value = Values[Math.Clamp(value, 0, Values.Length - 1)];
    }
}

public class IntParameter(int value, bool overridden = false) : VolumeParameter<int>(value, overridden)
{
    internal override void Interp(VolumeParameter to, float t) =>
        value = (int)MathF.Round(value + (((VolumeParameter<int>)to).Value - value) * t);
}

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

public class Vector3Parameter(Vector3 value, bool overridden = false) : VolumeParameter<Vector3>(value, overridden)
{
    internal override void Interp(VolumeParameter to, float t) =>
        value = Vector3.Lerp(value, ((VolumeParameter<Vector3>)to).Value, t);
}

public class ColorParameter(Vector3 value, bool hdr = false, bool overridden = false)
    : Vector3Parameter(value, overridden)
{
    public bool Hdr { get; } = hdr;
}
