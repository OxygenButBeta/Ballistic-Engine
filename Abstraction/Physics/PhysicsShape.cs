
namespace BallisticEngine;

// CPU-side collision shape descriptions, fully resolved (world scale already baked into the
// dimensions). The Engine layer's colliders build these; the physics backend turns them into
// its native shapes. No backend types leak through here.
public abstract record PhysicsShape;

// Full extents, not half extents (matches BoxCollider.Size).
public sealed record BoxShape(Vector3 Size) : PhysicsShape;

public sealed record SphereShape(float Radius) : PhysicsShape;

// Length is the cylindrical segment only; total height = Length + 2 * Radius. Axis is local Y.
public sealed record CapsuleShape(float Radius, float Length) : PhysicsShape;

// Triangle soup for static collision. Concave, so backends only accept it on
// static/kinematic bodies — never as part of a dynamic compound.
public sealed record MeshShape(Vector3[] Vertices, uint[] Indices, Vector3 Scale) : PhysicsShape;

// One shape of a body, posed relative to the body origin (the entity's world position). IsTrigger
// is PER-SHAPE: a single body can carry both solid and trigger parts (e.g. a character with a solid
// capsule and a trigger pickup-range sphere). The backend filters each compound child accordingly —
// solid children push, trigger children only report overlap (Unity's per-collider isTrigger).
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
