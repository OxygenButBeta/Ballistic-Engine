namespace BallisticEngine;

// The per-frame evaluated state of every VolumeComponent type (Unity's VolumeStack): one live
// instance per type, reset to engine defaults at the start of each blend pass, then pulled
// toward each contributing volume's overrides. Consumers (the renderer bridge) read the final
// values with GetComponent<T>().
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
