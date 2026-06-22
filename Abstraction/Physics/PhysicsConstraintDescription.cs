
namespace BallisticEngine;

public struct PhysicsConstraintDescription {
    public PhysicsConstraintType Type;
    public IPhysicsBody BodyA;
    public IPhysicsBody BodyB;

    public Vector3 LocalAnchorA;
    public Vector3 LocalAnchorB;

    public float Frequency;
    public float DampingRatio;

    public Vector3 Axis;
    public float TargetDistance;
    public float MinDistance;
    public float MaxDistance;
    public float MotorTargetVelocity;
    public float MotorMaxForce;
}
