
namespace BallisticEngine;

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
