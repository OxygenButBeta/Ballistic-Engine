
namespace BallisticEngine;

[Component("Slider Joint", "Physics")]
public class SliderJoint : Joint {
    [Tooltip("Line direction in this body's local space (normalized by the engine).")]
    public Vector3 Axis { get; set; } = Vector3.UnitX;

    protected override PhysicsConstraintType ConstraintType => PhysicsConstraintType.Slider;
    protected override void Configure(ref PhysicsConstraintDescription d) => d.Axis = Axis;
}
