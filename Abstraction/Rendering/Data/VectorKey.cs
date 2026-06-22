
namespace BallisticEngine;

public readonly struct VectorKey {
    public readonly float Time;
    public readonly Vector3 Value;
    public VectorKey(float time, Vector3 value) { Time = time; Value = value; }
}
