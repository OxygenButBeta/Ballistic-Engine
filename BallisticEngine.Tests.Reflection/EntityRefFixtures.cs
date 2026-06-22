using BallisticEngine;

namespace BallisticEngine.Tests.Reflection;

// Fixtures for the G1 "entity/component references" suite. A component carrying serializable scene-object
// references: an EntityRef (points at another Entity by InstanceId) and a ComponentRef (points at a
// Behaviour). Unlike a raw Entity/Behaviour field (which still drops loudly — G0), these value-type refs
// round-trip through the scene serializer as the target InstanceId hex and resolve lazily at runtime.
public sealed class RefHolderBehaviour : Behaviour {
    public EntityRef TargetEntity;        // set ref → round-trips as InstanceId hex
    public ComponentRef TargetComponent;  // set ref → round-trips as InstanceId hex
    public EntityRef UnsetEntity;          // default (None) → serialized to null, must NOT warn
    public int Marker = 11;                // a plain leaf alongside the refs
}

// A trivial target component for ComponentRef to point at.
public sealed class RefTargetBehaviour : Behaviour {
    public int Health = 100;
}
