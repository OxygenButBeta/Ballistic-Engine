using System.Reflection;

namespace BallisticEngine.Editor;

internal static class UserEditorWindowRegistry {
    public sealed class Entry {
        public string Key;
        public string MenuPath;
        public int Order;
        public EditorWindow Window;
    }

    static Entry[] cached;

    public static IReadOnlyList<Entry> Items {
        get {
            cached ??= Discover();
            return cached;
        }
    }

    public static void Rebuild() => cached = Discover();

    public static Entry Get(string key) {
        foreach (Entry e in Items)
            if (e.Key == key) return e;
        return null;
    }

    static Entry[] Discover() {
        var resolver = new DeterministicResolver<Entry>();
        foreach (Type type in TypeCache.GetTypesWithAttribute<EditorWindowMetaAttribute>()) {
            if (!typeof(EditorWindow).IsAssignableFrom(type))
                continue;

            var meta = type.GetCustomAttribute<EditorWindowMetaAttribute>();
            if (meta is null)
                continue;

            EditorWindow instance;
            try { instance = (EditorWindow)Activator.CreateInstance(type); }
            catch { continue; }

            instance.ConfigureFromMeta(type.FullName, meta);

            string key = type.FullName;
            string tieKey = $"{meta.MenuPath} {key}";
            var entry = new Entry { Key = key, MenuPath = meta.MenuPath, Order = meta.Order, Window = instance };
            resolver.Register(entry, priority: -meta.Order, tieKey: tieKey);
        }
        return resolver.All().ToArray();
    }

    public static void DrawAll(IEditorGui gui) {
        foreach (Entry e in Items)
            e.Window.DrawStandalone(gui);
    }

    static void ClearForReload() {
        if (cached != null)
            foreach (Entry e in cached)
                if (e.Window is IDisposable d) { try { d.Dispose(); } catch { } }
        cached = null;
    }

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RegisterReloadInvalidation() => ReloadCaches.Register(ClearForReload);
}
