
namespace BallisticEngine;

public class Vector3Parameter(Vector3 value, bool overridden = false) : VolumeParameter<Vector3>(value, overridden)
{
    internal override void Interp(VolumeParameter to, float t) =>
        value = Vector3.Lerp(value, ((VolumeParameter<Vector3>)to).Value, t);
}
