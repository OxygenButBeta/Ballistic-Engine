using System.Reflection;
using System.Runtime.CompilerServices;
using BallisticEngine.Editor.Inspector.Preview;

namespace BallisticEngine.Editor;

// The self-registering component-preview registry (editor-rework Rule 1 / Phase B1). REPLACES the
// `if (behaviour is Renderer/Volume/Terrain/AudioSource/Animator/...) DrawXxxSection(...)` god-chain that
// used to live inline in InspectorPanel.DrawComponent — the exact instanceof anti-pattern Rule 1 deletes.
// A preview self-registers for a component type via [ComponentPreview(typeof(Foo))]; the inspector asks this
// registry "what previews apply to type T" and draws them. Adding a custom section = adding one
// [ComponentPreview] class; InspectorPanel is never edited (it dropped to a thin component-list driver).
//
// This mirrors EditorWindowRegistry (A1) limb-for-limb — the project's established self-register pattern:
//   - DISCOVERY via TypeCache.GetTypesWithAttribute<ComponentPreviewAttribute>() (engine-side P0.1 substrate),
//     so previews in the host (editor) assembly are found by the same headless scan that finds [MenuItem]s and
//     game-script Behaviours, with zero editor-specific reflection.
//   - DETERMINISM (P0.4) via DeterministicResolver: among the previews applicable to a component type, the
//     order is HIGHER priority first, then the preview type's full name — a total, machine-independent
//     function of the registered set, never of assembly-load order.
//   - PERF (§4 / [[pref-no-reflection-render-hotpath]]): the scan + Activator.CreateInstance happen ONCE; the
//     per-component resolved list is memoized per component Type. The inspector redraws every ImGui frame and
//     pays ZERO reflection after warm-up.
//   - HOT-RELOAD (P0.3): the caches self-register their invalidation into the central ReloadCaches contract via
//     the [ModuleInitializer] below — a game-script reload (a script may [ComponentPreview] a game component)
//     drops the cached previews + per-type lists; the next resolve re-discovers over the rebuilt TypeCache.
internal static class ComponentPreviewRegistry {
    // One discovered preview: the component type it targets, its resolution priority, a stable tie-key, and the
    // (single, shared) instance that draws it. Previews are stateless shims — per-section state lives as statics
    // on InspectorPanel — so one instance per registered (type,target) pair is reused across every component.
    readonly struct Entry {
        public Entry(Type targetType, int priority, string tieKey, IComponentPreview preview) {
            TargetType = targetType;
            Priority = priority;
            TieKey = tieKey;
            Preview = preview;
        }
        public Type TargetType { get; }              // the component base/interface this preview applies to
        public int Priority { get; }
        public string TieKey { get; }
        public IComponentPreview Preview { get; }
    }

    static Entry[] discovered;                        // null = not yet scanned; Discover() fills it on first use
    // Memoized per-component-type resolution: the applicable previews in deterministic draw order. Cleared with
    // `discovered` on hot-reload. ComponentType -> the previews whose TargetType is assignable from it.
    static readonly Dictionary<Type, IComponentPreview[]> perTypeCache = new();

    // The previews applicable to a component type, in deterministic draw order (higher priority first, then
    // type full name). Empty array when none — the common case for a plain component, drawn member-by-member.
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

    // Force a (re)scan now. Harnesses call this after a fresh TypeCache.Build; the editor lets the first
    // PreviewsFor lazily trigger it (the first inspector draw, never a frame the user notices).
    public static void Rebuild() {
        discovered = Discover();
        perTypeCache.Clear();
    }

    static Entry[] Discover() {
        var entries = new List<Entry>();
        foreach (Type type in TypeCache.GetTypesWithAttribute<ComponentPreviewAttribute>()) {
            // Defensive: TypeCache already filters to concrete+instantiable, but the attribute can sit on any
            // class — only IComponentPreview implementors are usable.
            if (!typeof(IComponentPreview).IsAssignableFrom(type))
                continue;

            IComponentPreview instance;
            try { instance = (IComponentPreview)Activator.CreateInstance(type); }
            catch { continue; }   // a preview with a throwing ctor can't take the inspector down

            // AllowMultiple: one preview class may target several component types — one entry per attribute.
            foreach (ComponentPreviewAttribute attr in type.GetCustomAttributes<ComponentPreviewAttribute>()) {
                if (attr.TargetType is null)
                    continue;
                // Stable tie-break: target type + the preview's own full name, so two previews sharing a
                // priority for the same target still order identically on every machine.
                string tieKey = $"{attr.TargetType.FullName} {type.FullName}";
                entries.Add(new Entry(attr.TargetType, attr.Priority, tieKey, instance));
            }
        }
        return entries.ToArray();
    }

    // Hot-reload contract (P0.3): drop the discovered previews + per-type lists so the next resolve re-scans
    // over the rebuilt TypeCache. Self-registered via the [ModuleInitializer] below — joins TypeCache /
    // EditorWindowRegistry / DrawerStackPlan in ReloadCaches; the reload site never names this cache.
    static void ClearForReload() {
        discovered = null;
        perTypeCache.Clear();
    }

    [ModuleInitializer]
    internal static void RegisterReloadInvalidation() => ReloadCaches.Register(ClearForReload);
}
