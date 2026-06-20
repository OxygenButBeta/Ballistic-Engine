using System.Reflection;

namespace BallisticEngine.Editor;

// Discovers and hosts user-authored editor windows — the EditorWindow subclasses that carry
// [EditorWindowMeta] (built-in engine panels that need ctor args are registered explicitly elsewhere and
// do NOT carry it). This is the extension point that lets GAME developers add editor windows with zero
// shell wiring: subclass the public EditorWindow, fill OnGui(IEditorGui), mark the class with
// [EditorWindowMeta("Title")], and it appears under the Window menu — never touching ImGui or the shell.
//
// Discovery mirrors EditorWindowRegistry / AssetInspectorRegistry: a ONE-TIME TypeCache scan
// (GetTypesWithAttribute<EditorWindowMetaAttribute>), deterministically ordered, cached, and invalidated on
// game-script hot-reload via the [ModuleInitializer] below — so a window defined in GameEditorScripts is
// picked up the moment the editor reloads scripts, and a removed one drops cleanly (no stale ALC pin).
//
// Each discovered type is instantiated ONCE (it must have a public parameterless ctor — same contract as
// asset inspectors). The instance owns its Open flag (base EditorWindow.Open); the editor's Window menu
// toggles it through EditorWindows, and DrawAll routes each open window through WindowShell.DrawStandalone.
internal static class UserEditorWindowRegistry {
    // One discovered user window: its stable key (type FullName), menu placement, and the live instance.
    public sealed class Entry {
        public string Key;          // type FullName — the EditorWindows toggle key + ImGui ###id base
        public string MenuPath;     // "Window/My Tool"
        public int Order;
        public EditorWindow Window;  // the single live instance (Open flag lives here)
    }

    static Entry[] cached;   // null = not yet discovered

    // Every discovered user window, deterministically ordered. Built once; cleared on hot-reload.
    public static IReadOnlyList<Entry> Items {
        get {
            cached ??= Discover();
            return cached;
        }
    }

    public static void Rebuild() => cached = Discover();

    // Look up a discovered window by its key (type FullName). Null if none.
    public static Entry Get(string key) {
        foreach (Entry e in Items)
            if (e.Key == key) return e;
        return null;
    }

    static Entry[] Discover() {
        var resolver = new DeterministicResolver<Entry>();
        foreach (Type type in TypeCache.GetTypesWithAttribute<EditorWindowMetaAttribute>()) {
            // Only EditorWindow subclasses are hostable; the attribute could sit on any class.
            if (!typeof(EditorWindow).IsAssignableFrom(type))
                continue;

            var meta = type.GetCustomAttribute<EditorWindowMetaAttribute>();
            if (meta is null)
                continue;

            EditorWindow instance;
            try { instance = (EditorWindow)Activator.CreateInstance(type); }
            catch { continue; }   // a window with a throwing/argful ctor can't take the editor down — skip it

            // Identity + presentation come from the attribute (the author needn't set DockKey in the ctor).
            instance.ConfigureFromMeta(type.FullName, meta);

            string key = type.FullName;
            string tieKey = $"{meta.MenuPath} {key}";
            var entry = new Entry { Key = key, MenuPath = meta.MenuPath, Order = meta.Order, Window = instance };
            // DeterministicResolver prefers HIGHER priority; menu Order is "lower shows first", so negate.
            resolver.Register(entry, priority: -meta.Order, tieKey: tieKey);
        }
        return resolver.All().ToArray();
    }

    // Draw every open user window as a standalone floating window through WindowShell. Called once per frame
    // from the editor's window-draw block (after the built-in standalone panels).
    public static void DrawAll(IEditorGui gui) {
        foreach (Entry e in Items)
            e.Window.DrawStandalone(gui);
    }

    // Hot-reload contract (P0.3): drop the cached instances so the next access re-discovers over the rebuilt
    // TypeCache. Self-registered via the [ModuleInitializer] — joins TypeCache / the other reflection caches
    // in ReloadCaches, so a game-script reload re-scans without the reload site naming this registry.
    static void ClearForReload() => cached = null;

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RegisterReloadInvalidation() => ReloadCaches.Register(ClearForReload);
}
