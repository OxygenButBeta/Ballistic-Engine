
namespace BallisticEngine;

[Component("Fixed Joint", "Physics")]
public class FixedJoint : Joint {
    protected override PhysicsConstraintType ConstraintType => PhysicsConstraintType.Fixed;
}
