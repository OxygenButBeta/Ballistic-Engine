
namespace BallisticEngine;

public readonly struct Collision {
    public readonly Collider Collider;

    public readonly Rigidbody Rigidbody;

    public readonly Entity Entity;

    public readonly Vector3 Point;

    public readonly Vector3 Normal;

    internal Collision(Collider collider, Rigidbody rigidbody, Entity entity, Vector3 point, Vector3 normal) {
        Collider = collider;
        Rigidbody = rigidbody;
        Entity = entity;
        Point = point;
        Normal = normal;
    }
}
