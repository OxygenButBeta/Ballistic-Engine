
namespace BallisticEngine;

public struct RaycastHit {
    public Vector3 Point;
    public Vector3 Normal;
    public float Distance;
    public Collider Collider;
    public Rigidbody Rigidbody;
    public Entity Entity;
}
