
namespace BallisticEngine;

[Component("Hinge Joint", "Physics")]
public class HingeJoint : Joint {
    [Tooltip("Rotation axis in this body's local space (normalized by the engine).")]
    public Vector3 Axis { get; set; } = Vector3.UnitY;

    protected override PhysicsConstraintType ConstraintType => PhysicsConstraintType.Hinge;
    protected override void Configure(ref PhysicsConstraintDescription d) => d.Axis = Axis;
}
