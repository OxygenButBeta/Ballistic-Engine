
namespace BallisticEngine;

public class IntParameter(int value, bool overridden = false) : VolumeParameter<int>(value, overridden)
{
    internal override void Interp(VolumeParameter to, float t) =>
        value = (int)MathF.Round(value + (((VolumeParameter<int>)to).Value - value) * t);
}
