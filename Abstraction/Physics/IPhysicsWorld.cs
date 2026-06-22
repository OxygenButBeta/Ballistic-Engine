
namespace BallisticEngine;

public enum PhysicsBodyType {
    Dynamic,
    Kinematic,
    Static,
}

public struct PhysicsBodyDescription {
    public PhysicsBodyType Type;
    public Vector3 Position;
    public Quaternion Rotation;
    public float Mass;
    public float Friction;
    public float Bounciness;
    public bool FreezeRotation;
    public bool IsTrigger;
    public int Layer;
    public PhysicsShapePart[] Shapes;
}

public enum PhysicsContactPhase {
    Enter,
    Stay,
    Exit,
}

public struct PhysicsContactEvent {
    public PhysicsContactPhase Phase;
    public IPhysicsBody A;
    public IPhysicsBody B;
    public Vector3 Point;
    public Vector3 Normal;
    public bool IsTrigger;
    public int ChildA;

    public int ChildB;
}

public struct PhysicsRayHit {
    public Vector3 Point;
    public Vector3 Normal;
    public float Distance;
    public IPhysicsBody Body;
}

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
