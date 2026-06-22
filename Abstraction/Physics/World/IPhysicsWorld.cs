
namespace BallisticEngine;

public interface IPhysicsWorld {
    Vector3 Gravity { get; set; }

    Func<int, int, bool> LayerCollisionMatrix { get; set; }

    IPhysicsBody AddBody(in PhysicsBodyDescription description);

    void RemoveBody(IPhysicsBody body);

    void Step(float deltaTime);

    IReadOnlyList<PhysicsContactEvent> ContactEvents { get; }

    bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, int layerMask, out PhysicsRayHit hit);

    bool ShapeCast(PhysicsShape shape, Vector3 position, Quaternion rotation, Vector3 direction,
        float maxDistance, int layerMask, out PhysicsRayHit hit);

    int OverlapSphere(Vector3 center, float radius, int layerMask, List<IPhysicsBody> results);
    int OverlapBox(Vector3 center, Vector3 halfExtents, Quaternion orientation, int layerMask,
        List<IPhysicsBody> results);

    int OverlapShape(PhysicsShape shape, Vector3 position, Quaternion rotation, int layerMask,
        List<IPhysicsBody> results);

    IPhysicsConstraint AddConstraint(in PhysicsConstraintDescription description);
    void RemoveConstraint(IPhysicsConstraint constraint);

    void Reset();
}
