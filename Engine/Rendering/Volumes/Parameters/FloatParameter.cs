
namespace BallisticEngine;

public class FloatParameter(float value, bool overridden = false) : VolumeParameter<float>(value, overridden)
{
    internal override void Interp(VolumeParameter to, float t) =>
        value += (((VolumeParameter<float>)to).Value - value) * t;
}
