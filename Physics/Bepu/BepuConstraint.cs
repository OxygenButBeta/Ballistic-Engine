using BepuPhysics;

namespace BallisticEngine.Bepu;

// Wraps one Bepu constraint handle. After Invalidate() (removed / world reset) every member is a
// safe no-op, mirroring BepuBody. If this constraint created a private world-anchor body (BodyB was
// null), that anchor is owned here and torn down with the constraint.
sealed class BepuConstraint : IPhysicsConstraint {
    readonly BepuPhysicsWorld world;
    internal readonly ConstraintHandle Handle;
    // A kinematic anchor body created solely to give a world-anchored constraint its second body.
    // BodyHandle.Value < 0 (default) when there is no private anchor.
    internal readonly BodyHandle AnchorBody;
    internal readonly bool HasAnchor;
    // The constrained bodies (B null for a world anchor), kept so removal can wake them — a body that
    // slept while the joint held it must resume falling once freed.
    internal readonly BepuBody BodyA, BodyB;

    bool valid = true;

    public object UserData { get; set; }
    public bool IsValid => valid;

    internal BepuConstraint(BepuPhysicsWorld world, ConstraintHandle handle, BodyHandle anchorBody,
        bool hasAnchor, BepuBody bodyA, BepuBody bodyB) {
        this.world = world;
        Handle = handle;
        AnchorBody = anchorBody;
        HasAnchor = hasAnchor;
        BodyA = bodyA;
        BodyB = bodyB;
    }

    internal void Invalidate() => valid = false;
}
