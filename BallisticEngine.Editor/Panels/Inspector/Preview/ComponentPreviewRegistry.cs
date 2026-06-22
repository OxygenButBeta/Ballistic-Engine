using System.Reflection;
using System.Runtime.CompilerServices;
using BallisticEngine.Editor.Inspector.Preview;

namespace BallisticEngine.Editor;

internal static class ComponentPreviewRegistry {
    readonly struct Entry {
        public Entry(Type targetType, int priority, string tieKey, IComponentPreview preview) {
            TargetType = targetType;
            Priority = priority;
            TieKey = tieKey;
            Preview = preview;
        }
        public Type TargetType { get; }
        public int Priority { get; }
        public string TieKey { get; }
        public IComponentPreview Preview { get; }
    }

    static Entry[] discovered;

    static readonly Dictionary<Type, IComponentPreview[]> perTypeCache = new();

    public static IReadOnlyList<IComponentPreview> PreviewsFor(Type componentType) {
        if (componentType is null)
            return Array.Empty<IComponentPreview>();
        if (perTypeCache.TryGetValue(componentType, out IComponentPreview[] cached))
            return cached;

        discovered ??= Discover();
        var resolver = new DeterministicResolver<IComponentPreview>();
        foreach (Entry e in discovered)
            if (e.TargetType.IsAssignableFrom(componentType))
                resolver.Register(e.Preview, priority: e.Priority, tieKey: e.TieKey);

        IComponentPreview[] result = resolver.All().ToArray();
        perTypeCache[componentType] = result;
        return result;
    }

    public static void Rebuild() {
        discovered = Discover();
        perTypeCache.Clear();
    }

    static Entry[] Discover() {
        var entries = new List<Entry>();
        foreach (Type type in TypeCache.GetTypesWithAttribute<ComponentPreviewAttribute>()) {
            if (!typeof(IComponentPreview).IsAssignableFrom(type))
                continue;

            IComponentPreview instance;
            try { instance = (IComponentPreview)Activator.CreateInstance(type); }
            catch { continue; }

            foreach (ComponentPreviewAttribute attr in type.GetCustomAttributes<ComponentPreviewAttribute>()) {
                if (attr.TargetType is null)
                    continue;
                string tieKey = $"{attr.TargetType.FullName} {type.FullName}";
                entries.Add(new Entry(attr.TargetType, attr.Priority, tieKey, instance));
            }
        }
        return entries.ToArray();
    }

    static void ClearForReload() {
        discovered = null;
        perTypeCache.Clear();
    }

    [ModuleInitializer]
    internal static void RegisterReloadInvalidation() => ReloadCaches.Register(ClearForReload);
}
