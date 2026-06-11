using OpenTK.Mathematics;

namespace BallisticEngine;

// One overridable value inside a VolumeComponent (Unity's VolumeParameter). A parameter only
// influences the blended stack while Overridden is true; Interp defines how the stack's current
// value moves toward this volume's value under a 0..1 blend factor (camera inside a local box,
// volume weight, ...). Non-interpolatable types (bool, enums) snap to the target for any t > 0.
public abstract class VolumeParameter {
    public bool Overridden { get; set; }

    // `this` is the stack's working value; `to` is the overriding volume's parameter.
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

public class BoolParameter : VolumeParameter<bool> {
    public BoolParameter(bool value, bool overridden = false) : base(value, overridden) { }
}

public class FloatParameter : VolumeParameter<float> {
    public FloatParameter(float value, bool overridden = false) : base(value, overridden) { }

    internal override void Interp(VolumeParameter to, float t) =>
        value += (((VolumeParameter<float>)to).Value - value) * t;
}

// Float clamped to [Min, Max]; the editor shows it as a slider over that range.
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

// Non-generic view of EnumParameter<T> so the editor (dropdown) and the .volume serializer
// (name string) can handle any enum without knowing T.
public interface IEnumParameter {
    string[] Names { get; }
    int Index { get; set; }
}

// Enum choice; snaps to the target under blending like every non-numeric parameter.
public class EnumParameter<T> : VolumeParameter<T>, IEnumParameter where T : struct, Enum {
    static readonly T[] Values = Enum.GetValues<T>();
    static readonly string[] ValueNames = Enum.GetNames<T>();

    public EnumParameter(T value, bool overridden = false) : base(value, overridden) { }

    public string[] Names => ValueNames;

    public int Index {
        get => Array.IndexOf(Values, value);
        set => this.value = Values[Math.Clamp(value, 0, Values.Length - 1)];
    }
}

public class IntParameter : VolumeParameter<int> {
    public IntParameter(int value, bool overridden = false) : base(value, overridden) { }

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

public class Vector3Parameter : VolumeParameter<Vector3> {
    public Vector3Parameter(Vector3 value, bool overridden = false) : base(value, overridden) { }

    internal override void Interp(VolumeParameter to, float t) =>
        value = Vector3.Lerp(value, ((VolumeParameter<Vector3>)to).Value, t);
}

// Vector3 drawn as a color picker in the editor (Hdr allows components > 1).
public class ColorParameter : Vector3Parameter {
    public bool Hdr { get; }

    public ColorParameter(Vector3 value, bool hdr = false, bool overridden = false)
        : base(value, overridden) {
        Hdr = hdr;
    }
}
