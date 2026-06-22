
namespace BallisticEngine;

[Component("Spring Joint", "Physics")]
public class SpringJoint : Joint {
    [Tooltip("Rest separation the spring pulls toward, in metres.")]
    [Range(0f, 100f)]
    public float TargetDistance { get; set; } = 1f;

    protected override PhysicsConstraintType ConstraintType => PhysicsConstraintType.Spring;
    protected override void Configure(ref PhysicsConstraintDescription d) => d.TargetDistance = TargetDistance;
}
