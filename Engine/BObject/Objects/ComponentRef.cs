namespace BallisticEngine;

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
