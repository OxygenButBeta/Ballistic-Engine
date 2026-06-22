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
