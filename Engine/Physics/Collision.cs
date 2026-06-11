using OpenTK.Mathematics;

namespace BallisticEngine;

// What hit you (Unity's Collision, slimmed to one representative contact). Passed to the
// OnCollision* callbacks; all members describe the OTHER side of the contact.
public readonly struct Collision {
    // The other collider. Null when the other body is a collider-less Rigidbody fallback.
    public readonly Collider Collider;

    // The other rigidbody. Null when the other side is static level geometry.
    public readonly Rigidbody Rigidbody;

    // The other entity.
    public readonly Entity Entity;

    // World-space contact point (last known one for OnCollisionExit).
    public readonly Vector3 Point;

    // Unit contact normal pointing from the other object toward this one.
    public readonly Vector3 Normal;

    internal Collision(Collider collider, Rigidbody rigidbody, Entity entity, Vector3 point, Vector3 normal) {
        Collider = collider;
        Rigidbody = rigidbody;
        Entity = entity;
        Point = point;
        Normal = normal;
    }
}
