
namespace BallisticEngine;

// Base for physics joints (P6): binds this entity's Rigidbody to another body (or the world) with a
// constraint. Subclasses pick the constraint type and fill type-specific settings. Play-mode only,
// like Rigidbody. The constraint is created LAZILY on the first FixedTick where BOTH bodies exist —
// this side-steps the ordering problem (the connected Rigidbody may create its body after this Joint's
// OnEnabled), and naturally retries until both bodies are live.
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

    // Subclasses set their type-specific fields (axis, target distance, limits) on the description.
    protected virtual void Configure(ref PhysicsConstraintDescription description) {
    }

    protected internal override void OnDisabled() => DestroyConstraint();
    protected internal override void OnDetach() => DestroyConstraint();

    // Lazy creation: runs every fixed step until the constraint exists. Both this entity's Rigidbody
    // and the connected Rigidbody (if any) must have live bodies — guaranteed by the second fixed step
    // at the latest, regardless of component/entity order.
    protected internal override void FixedTick(in float deltaTime) {
        if (constraint is not null || !SceneManager.IsPlaying)
            return;

        Rigidbody self = GetComponent<Rigidbody>();
        IPhysicsBody bodyA = self?.InternalBody;
        if (bodyA is null)
            return; // no Rigidbody on this entity, or its body isn't created yet

        IPhysicsBody bodyB = null;
        if (ConnectedBody is not null) {
            if (ConnectedBody.Entity is null || ConnectedBody.Entity.IsDestroyed)
                return;
            bodyB = ConnectedBody.InternalBody;
            if (bodyB is null)
                return; // connected body not ready yet — retry next step
        }

        var description = new PhysicsConstraintDescription {
            Type = ConstraintType,
            BodyA = bodyA,
            BodyB = bodyB, // null = world anchor
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

// ---- Concrete joints --------------------------------------------------------

// Point-to-point: the two anchor points are held together, rotation free. Rope links, ragdoll joints.
[Component("Ball Socket Joint", "Physics")]
public class BallSocketJoint : Joint {
    protected override PhysicsConstraintType ConstraintType => PhysicsConstraintType.BallSocket;
}

// Revolute: shared point + a single locked rotation axis. Doors, levers, wheel mounts, elbows.
[Component("Hinge Joint", "Physics")]
public class HingeJoint : Joint {
    [Tooltip("Rotation axis in this body's local space (normalized by the engine).")]
    public Vector3 Axis { get; set; } = Vector3.UnitY;

    protected override PhysicsConstraintType ConstraintType => PhysicsConstraintType.Hinge;
    protected override void Configure(ref PhysicsConstraintDescription d) => d.Axis = Axis;
}

// Weld: fully locks the current relative pose of the two bodies (rigid attach).
[Component("Fixed Joint", "Physics")]
public class FixedJoint : Joint {
    protected override PhysicsConstraintType ConstraintType => PhysicsConstraintType.Fixed;
}

// Distance spring: pulled toward TargetDistance with the joint's spring. Suspension, bungee, tether.
[Component("Spring Joint", "Physics")]
public class SpringJoint : Joint {
    [Tooltip("Rest separation the spring pulls toward, in metres.")]
    [Range(0f, 100f)]
    public float TargetDistance { get; set; } = 1f;

    protected override PhysicsConstraintType ConstraintType => PhysicsConstraintType.Spring;
    protected override void Configure(ref PhysicsConstraintDescription d) => d.TargetDistance = TargetDistance;
}

// Point-on-line: the connected point is confined to a line along Axis through this body's anchor.
// Pistons, drawers, elevators.
[Component("Slider Joint", "Physics")]
public class SliderJoint : Joint {
    [Tooltip("Line direction in this body's local space (normalized by the engine).")]
    public Vector3 Axis { get; set; } = Vector3.UnitX;

    protected override PhysicsConstraintType ConstraintType => PhysicsConstraintType.Slider;
    protected override void Configure(ref PhysicsConstraintDescription d) => d.Axis = Axis;
}
