namespace BallisticEngine;

// Serializable references to runtime SCENE objects (an Entity or a Behaviour/Component), the way a Unity
// [SerializeField] GameObject / Component field works. The engine's existing BObject-asset references
// round-trip as "guid:<hex>" because assets live in the AssetDatabase; scene objects do NOT have an asset
// guid (they are created at scene load), so they are identified by their InstanceId instead — exactly the
// shape BEvent already uses for its persistent-listener targets (stored as InstanceId, resolved at runtime
// via SceneManager.FindByInstanceId).
//
// These are VALUE types holding only the target InstanceId. Resolution is LAZY (resolve on access, like
// BEvent.ResolveTarget) so deserialization never depends on entity creation order: a ref can be assigned
// before its target exists in the scene, and the first .Value access binds it once the scene is fully
// built. A ref to a deleted / not-in-scene object resolves to null, Unity's "missing reference" behaviour.
//
// The scene serializer writes/reads the InstanceId hex directly (SceneSerializer.SerializeValue /
// DeserializeValue have a dedicated case BEFORE the BObject-asset case). The editor renders a scene-object
// picker for these (PropertyCategory.SceneObjectRef); a raw Entity/Behaviour field — which has no place to
// store the InstanceId across save/load — still drops loudly (G0), steering authors to EntityRef instead.

// A serializable reference to an Entity by InstanceId. Resolves lazily to the live Entity in the current
// scene; null when the target is missing.
public readonly struct EntityRef {
    // The target Entity's InstanceId (Guid.Empty = no reference / "None"). Public so the serializer and the
    // editor picker read it without reflection; the struct is immutable (assign a new ref to retarget).
    public readonly Guid InstanceId;

    public EntityRef(Guid instanceId) => InstanceId = instanceId;

    public EntityRef(Entity entity) => InstanceId = entity?.InstanceId ?? Guid.Empty;

    // True when a target is set (it may still resolve to null if that target was deleted — like Unity, a
    // set-but-missing ref). Use Value == null to test live resolvability.
    public bool HasValue => InstanceId != Guid.Empty;

    // The live Entity, resolved against the current scene each access (cheap linear scan, cached by callers
    // that need it hot — same contract as BEvent.ResolveTarget). Null when unset or the target is gone.
    public Entity Value => InstanceId == Guid.Empty ? null : SceneManager.FindByInstanceId(InstanceId) as Entity;

    public static readonly EntityRef None = default;

    public static implicit operator EntityRef(Entity entity) => new(entity);
    public static implicit operator Entity(EntityRef reference) => reference.Value;

    public override string ToString() => InstanceId == Guid.Empty ? "EntityRef(None)" : $"EntityRef({InstanceId:N})";
}

// A serializable reference to a Behaviour/Component by InstanceId. Resolves lazily to the live Behaviour in
// the current scene; null when missing. Use Get<T>() for a typed accessor.
public readonly struct ComponentRef {
    // The target Behaviour's InstanceId (Guid.Empty = no reference / "None").
    public readonly Guid InstanceId;

    public ComponentRef(Guid instanceId) => InstanceId = instanceId;

    public ComponentRef(Behaviour behaviour) => InstanceId = behaviour?.InstanceId ?? Guid.Empty;

    public bool HasValue => InstanceId != Guid.Empty;

    // The live Behaviour, resolved against the current scene each access. Null when unset or missing.
    public Behaviour Value => InstanceId == Guid.Empty ? null : SceneManager.FindByInstanceId(InstanceId) as Behaviour;

    // Typed accessor: the live Behaviour cast to T, or null if unset / missing / the wrong concrete type.
    public T Get<T>() where T : Behaviour => Value as T;

    public static readonly ComponentRef None = default;

    public static implicit operator ComponentRef(Behaviour behaviour) => new(behaviour);
    public static implicit operator Behaviour(ComponentRef reference) => reference.Value;

    public override string ToString() => InstanceId == Guid.Empty ? "ComponentRef(None)" : $"ComponentRef({InstanceId:N})";
}
