namespace BallisticEngine;

public sealed class VolumeStack {
    readonly Dictionary<Type, VolumeComponent> lookup = new();
    readonly List<(VolumeComponent Live, VolumeComponent Defaults)> entries = new();

    internal VolumeStack(IEnumerable<Type> componentTypes) {
        foreach (Type type in componentTypes) {
            if (lookup.ContainsKey(type))
                continue;

            var live = (VolumeComponent)Activator.CreateInstance(type);
            var defaults = (VolumeComponent)Activator.CreateInstance(type);
            lookup[type] = live;
            entries.Add((live, defaults));
        }
    }

    public T GetComponent<T>() where T : VolumeComponent =>
        lookup.TryGetValue(typeof(T), out VolumeComponent component) ? (T)component : null;

    internal VolumeComponent Get(Type type) => lookup.GetValueOrDefault(type);

    internal void Reset() {
        foreach ((VolumeComponent live, VolumeComponent defaults) in entries)
            live.CopyValuesFrom(defaults);
    }
}
