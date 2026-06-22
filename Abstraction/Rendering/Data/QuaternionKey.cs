
namespace BallisticEngine;

public readonly struct QuaternionKey {
    public readonly float Time;
    public readonly Quaternion Value;
    public QuaternionKey(float time, Quaternion value) { Time = time; Value = value; }
}
