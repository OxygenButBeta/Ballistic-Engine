namespace BallisticEngine;

public readonly struct EntityRef {
    public readonly Guid InstanceId;

    public EntityRef(Guid instanceId) => InstanceId = instanceId;

    public EntityRef(Entity entity) => InstanceId = entity?.InstanceId ?? Guid.Empty;

    public bool HasValue => InstanceId != Guid.Empty;

    public Entity Value => InstanceId == Guid.Empty ? null : SceneManager.FindByInstanceId(InstanceId) as Entity;

    public static readonly EntityRef None = default;

    public static implicit operator EntityRef(Entity entity) => new(entity);
    public static implicit operator Entity(EntityRef reference) => reference.Value;

    public override string ToString() => InstanceId == Guid.Empty ? "EntityRef(None)" : $"EntityRef({InstanceId:N})";
}

public readonly struct ComponentRef {
    public readonly Guid InstanceId;

    public ComponentRef(Guid instanceId) => InstanceId = instanceId;

    public ComponentRef(Behaviour behaviour) => InstanceId = behaviour?.InstanceId ?? Guid.Empty;

    public bool HasValue => InstanceId != Guid.Empty;

    public Behaviour Value => InstanceId == Guid.Empty ? null : SceneManager.FindByInstanceId(InstanceId) as Behaviour;

    public T Get<T>() where T : Behaviour => Value as T;

    public static readonly ComponentRef None = default;

    public static implicit operator ComponentRef(Behaviour behaviour) => new(behaviour);
    public static implicit operator Behaviour(ComponentRef reference) => reference.Value;

    public override string ToString() => InstanceId == Guid.Empty ? "ComponentRef(None)" : $"ComponentRef({InstanceId:N})";
}
