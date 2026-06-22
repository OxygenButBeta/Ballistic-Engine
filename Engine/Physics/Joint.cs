
namespace BallisticEngine;

public abstract class Joint : Behaviour {
    [Tooltip("The other body to attach to. Leave empty to anchor this body to the WORLD (a fixed point).")]
    public Rigidbody ConnectedBody { get; set; }

    [Tooltip("Attach point on THIS body, in local space.")]
    public Vector3 Anchor { get; set; } = Vector3.Zero;

    [Tooltip("Attach point on the connected body (or the world point), in its local space.")]
    public Vector3 ConnectedAnchor { get; set; } = Vector3.Zero;

    [Header("Spring")]
    [Tooltip("Constraint stiffness in Hz. 0 = rigid default. Higher = stiffer/snappier.")]
    [Range(0f, 120f)]
    public float Frequency { get; set; }

    [Tooltip("1 = critically damped (no overshoot), 0 = bouncy. Used with a nonzero Frequency.")]
    [Range(0f, 2f)]
    public float DampingRatio { get; set; } = 1f;

    IPhysicsConstraint constraint;

    protected abstract PhysicsConstraintType ConstraintType { get; }

    protected virtual void Configure(ref PhysicsConstraintDescription description) {
    }

    protected internal override void OnDisabled() => DestroyConstraint();
    protected internal override void OnDetach() => DestroyConstraint();

    protected internal override void FixedTick(in float deltaTime) {
        if (constraint is not null || !SceneManager.IsPlaying)
            return;

        Rigidbody self = GetComponent<Rigidbody>();
        IPhysicsBody bodyA = self?.InternalBody;
        if (bodyA is null)
            return;

        IPhysicsBody bodyB = null;
        if (ConnectedBody is not null) {
            if (ConnectedBody.Entity is null || ConnectedBody.Entity.IsDestroyed)
                return;
            bodyB = ConnectedBody.InternalBody;
            if (bodyB is null)
                return;
        }

        var description = new PhysicsConstraintDescription {
            Type = ConstraintType,
            BodyA = bodyA,
            BodyB = bodyB,
            LocalAnchorA = Anchor,
            LocalAnchorB = ConnectedAnchor,
            Frequency = Frequency,
            DampingRatio = DampingRatio,
        };
        Configure(ref description);

        constraint = Physics.World?.AddConstraint(in description);
        if (constraint is not null)
            constraint.UserData = this;
    }

    void DestroyConstraint() {
        if (constraint is null)
            return;
        Physics.World?.RemoveConstraint(constraint);
        constraint = null;
    }
}

[Component("Ball Socket Joint", "Physics")]
public class BallSocketJoint : Joint {
    protected override PhysicsConstraintType ConstraintType => PhysicsConstraintType.BallSocket;
}

[Component("Hinge Joint", "Physics")]
public class HingeJoint : Joint {
    [Tooltip("Rotation axis in this body's local space (normalized by the engine).")]
    public Vector3 Axis { get; set; } = Vector3.UnitY;

    protected override PhysicsConstraintType ConstraintType => PhysicsConstraintType.Hinge;
    protected override void Configure(ref PhysicsConstraintDescription d) => d.Axis = Axis;
}

[Component("Fixed Joint", "Physics")]
public class FixedJoint : Joint {
    protected override PhysicsConstraintType ConstraintType => PhysicsConstraintType.Fixed;
}

[Component("Spring Joint", "Physics")]
public class SpringJoint : Joint {
    [Tooltip("Rest separation the spring pulls toward, in metres.")]
    [Range(0f, 100f)]
    public float TargetDistance { get; set; } = 1f;

    protected override PhysicsConstraintType ConstraintType => PhysicsConstraintType.Spring;
    protected override void Configure(ref PhysicsConstraintDescription d) => d.TargetDistance = TargetDistance;
}

[Component("Slider Joint", "Physics")]
public class SliderJoint : Joint {
    [Tooltip("Line direction in this body's local space (normalized by the engine).")]
    public Vector3 Axis { get; set; } = Vector3.UnitX;

    protected override PhysicsConstraintType ConstraintType => PhysicsConstraintType.Slider;
    protected override void Configure(ref PhysicsConstraintDescription d) => d.Axis = Axis;
}
