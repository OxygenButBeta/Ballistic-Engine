namespace BallisticEngine;

public sealed class VolumeProfile : BObject {
    readonly List<VolumeComponent> components = new();

    public IReadOnlyList<VolumeComponent> Components => components;

    public T Add<T>() where T : VolumeComponent, new() => (T)Add(typeof(T));

    public VolumeComponent Add(Type type) {
        if (Get(type) is { } existing)
            return existing;

        var component = (VolumeComponent)Activator.CreateInstance(type);
        components.Add(component);
        return component;
    }

    public bool Has(Type type) => Get(type) is not null;

    public VolumeComponent Get(Type type) {
        foreach (VolumeComponent component in components)
            if (component.GetType() == type)
                return component;
        return null;
    }

    public bool TryGet<T>(out T component) where T : VolumeComponent {
        component = (T)Get(typeof(T));
        return component is not null;
    }

    public bool Remove(VolumeComponent component) => components.Remove(component);
}
