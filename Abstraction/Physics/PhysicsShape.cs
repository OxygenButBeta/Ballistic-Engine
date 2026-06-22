
namespace BallisticEngine;

public abstract record PhysicsShape;

public sealed record BoxShape(Vector3 Size) : PhysicsShape;

public sealed record SphereShape(float Radius) : PhysicsShape;

public sealed record CapsuleShape(float Radius, float Length) : PhysicsShape;

public sealed record MeshShape(Vector3[] Vertices, uint[] Indices, Vector3 Scale) : PhysicsShape;

public readonly struct PhysicsShapePart {
    public readonly PhysicsShape Shape;
    public readonly Vector3 LocalPosition;
    public readonly Quaternion LocalRotation;
    public readonly bool IsTrigger;

    public PhysicsShapePart(PhysicsShape shape, Vector3 localPosition, Quaternion localRotation,
        bool isTrigger = false) {
        Shape = shape;
        LocalPosition = localPosition;
        LocalRotation = localRotation;
        IsTrigger = isTrigger;
    }
}
