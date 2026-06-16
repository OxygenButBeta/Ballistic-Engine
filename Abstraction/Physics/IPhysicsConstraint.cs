
namespace BallisticEngine;

// A joint/constraint kind. The backend maps each to its native constraint (BepuPhysics structs).
public enum PhysicsConstraintType {
    BallSocket, // point-to-point: bodies share a world point, free rotation (rope link, ragdoll joint)
    Hinge,      // revolute: shared point + a locked rotation axis (door, wheel mount, elbow)
    Fixed,      // weld: relative pose fully locked (rigid attach of two bodies)
    Spring,     // distance servo: pulled toward a target separation with a spring (suspension, bungee)
    Slider,     // point-on-line: B's point confined to a line through A along an axis (piston, drawer)
}

// One constraint to create, fully resolved (anchors in each body's LOCAL space). BodyB null = anchor
// to the world (the backend supplies a fixed kinematic anchor). A single struct rather than a method
// per type keeps the interface small; type-specific fields are read only for the matching Type.
public struct PhysicsConstraintDescription {
    public PhysicsConstraintType Type;
    public IPhysicsBody BodyA;          // required
    public IPhysicsBody BodyB;          // null = world anchor

    public Vector3 LocalAnchorA;        // attach point on A, A-local
    public Vector3 LocalAnchorB;        // attach point on B (or world point when BodyB is null), B-local

    // Stiffness of the constraint spring: frequency in Hz, damping ratio (1 = critically damped/rigid,
    // 0 = bouncy). Defaults (0,0) tell the backend to use a rigid default.
    public float Frequency;
    public float DampingRatio;

    // Type-specific.
    public Vector3 Axis;                // Hinge: rotation axis (A-local). Slider: line direction (A-local).
    public float TargetDistance;        // Spring: rest separation. Slider: ignored.
    public float MinDistance;           // Slider/Spring-as-limit: range min.
    public float MaxDistance;           // Slider: range max along the axis.
    public float MotorTargetVelocity;   // Hinge/Slider motor target (rad/s or m/s); 0 = no motor.
    public float MotorMaxForce;         // motor force cap; 0 with a nonzero target = unlimited.
}

// A live constraint in the physics world. Safe no-ops after removal or world reset.
public interface IPhysicsConstraint {
    bool IsValid { get; }
    object UserData { get; set; } // engine sets the owning Joint component
}
