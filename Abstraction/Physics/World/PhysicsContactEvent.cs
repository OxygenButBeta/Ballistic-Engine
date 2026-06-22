
namespace BallisticEngine;

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
