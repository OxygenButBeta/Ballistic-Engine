
namespace BallisticEngine;

public interface IPhysicsBody {
    bool IsStatic { get; }

    bool IsAwake { get; }

    Vector3 Position { get; set; }
    Quaternion Rotation { get; set; }

    Vector3 LinearVelocity { get; set; }
    Vector3 AngularVelocity { get; set; }

    void ApplyImpulse(Vector3 impulse);
    void ApplyImpulse(Vector3 impulse, Vector3 worldPoint);
    void ApplyAngularImpulse(Vector3 impulse);
    void WakeUp();

    object UserData { get; set; }
}
