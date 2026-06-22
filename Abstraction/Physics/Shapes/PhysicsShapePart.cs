
namespace BallisticEngine;

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
