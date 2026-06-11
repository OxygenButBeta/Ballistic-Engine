using OpenTK.Mathematics;

namespace BallisticEngine;

// A live body in the physics world. Position/Rotation are the BODY ORIGIN (the entity's world
// pose) — backends that recenter compound shapes around the center of mass hide that offset
// behind this interface. All members are safe no-ops after the body is removed or the world
// is reset, so component teardown order never crashes.
public interface IPhysicsBody {
    bool IsStatic { get; }

    // False while the body sleeps. Writers that merely maintain state each step (damping)
    // should skip sleeping bodies instead of waking them.
    bool IsAwake { get; }

    Vector3 Position { get; set; }
    Quaternion Rotation { get; set; }

    Vector3 LinearVelocity { get; set; }
    Vector3 AngularVelocity { get; set; }

    void ApplyImpulse(Vector3 impulse);
    void ApplyImpulse(Vector3 impulse, Vector3 worldPoint);
    void ApplyAngularImpulse(Vector3 impulse);
    void WakeUp();

    // The engine sets this to the owning component (Rigidbody or Collider) so raycast hits
    // can be mapped back to entities. Opaque to the backend.
    object UserData { get; set; }
}
