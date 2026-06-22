using BepuPhysics;

namespace BallisticEngine.Bepu;

sealed class BepuConstraint : IPhysicsConstraint {
    readonly BepuPhysicsWorld world;
    internal readonly ConstraintHandle Handle;

    internal readonly BodyHandle AnchorBody;
    internal readonly bool HasAnchor;

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
