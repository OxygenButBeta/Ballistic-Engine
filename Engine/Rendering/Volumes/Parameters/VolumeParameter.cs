
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
