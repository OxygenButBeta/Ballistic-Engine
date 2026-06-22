using System.Reflection;
using System.Runtime.CompilerServices;
using BallisticEngine.Editor.Inspector.AssetInspectors;

namespace BallisticEngine.Editor;

internal static class AssetInspectorRegistry {
    readonly struct Entry {
        public Entry(string extension, int priority, string tieKey, IAssetInspector inspector) {
            Extension = extension;
            Priority = priority;
            TieKey = tieKey;
            Inspector = inspector;
        }
        public string Extension { get; }
        public int Priority { get; }
        public string TieKey { get; }
        public IAssetInspector Inspector { get; }
    }

    static Entry[] discovered;

    static readonly Dictionary<string, IAssetInspector> perExtCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static IAssetInspector InspectorFor(string extension) {
        if (string.IsNullOrEmpty(extension))
            return null;
        if (perExtCache.TryGetValue(extension, out IAssetInspector cached))
            return cached;

        discovered ??= Discover();
        var resolver = new DeterministicResolver<IAssetInspector>();
        foreach (Entry e in discovered)
            if (string.Equals(e.Extension, extension, StringComparison.OrdinalIgnoreCase))
                resolver.Register(e.Inspector, priority: e.Priority, tieKey: e.TieKey);

        IAssetInspector result = resolver.Resolve(_ => true);
        perExtCache[extension] = result;
        return result;
    }

    public static void Rebuild() {
        discovered = Discover();
        perExtCache.Clear();
    }

    static Entry[] Discover() {
        var entries = new List<Entry>();
        foreach (Type type in TypeCache.GetTypesWithAttribute<AssetInspectorAttribute>()) {
            if (!typeof(IAssetInspector).IsAssignableFrom(type))
                continue;

            IAssetInspector instance;
            try { instance = (IAssetInspector)Activator.CreateInstance(type); }
            catch { continue; }

            foreach (AssetInspectorAttribute attr in type.GetCustomAttributes<AssetInspectorAttribute>()) {
                if (string.IsNullOrEmpty(attr.Extension))
                    continue;
                string tieKey = $"{attr.Extension} {type.FullName}";
                entries.Add(new Entry(attr.Extension, attr.Priority, tieKey, instance));
            }
        }
        return entries.ToArray();
    }

    static void ClearForReload() {
        discovered = null;
        perExtCache.Clear();
    }

    [ModuleInitializer]
    internal static void RegisterReloadInvalidation() => ReloadCaches.Register(ClearForReload);
}
