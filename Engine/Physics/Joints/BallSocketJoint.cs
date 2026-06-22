
namespace BallisticEngine;

[Component("Ball Socket Joint", "Physics")]
public class BallSocketJoint : Joint {
    protected override PhysicsConstraintType ConstraintType => PhysicsConstraintType.BallSocket;
}
