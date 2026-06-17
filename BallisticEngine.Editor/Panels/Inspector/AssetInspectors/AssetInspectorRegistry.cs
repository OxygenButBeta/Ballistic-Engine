using System.Reflection;
using System.Runtime.CompilerServices;
using BallisticEngine.Editor.Inspector.AssetInspectors;

namespace BallisticEngine.Editor;

// The self-registering asset-inspector registry (editor-rework Rule 1 / Phase B2). REPLACES the
// `switch (ext) { case ".mat": DrawMaterialEditor(...); case ".png" or ...: DrawTextureImportSettings(...);
// case ".volume": DrawVolumeProfileAsset(...); ... }` god-switch in InspectorPanel.DrawAssetInspector — the
// asset-side mirror of B1's ComponentPreviewRegistry (which deleted the `if (behaviour is Renderer/Volume/...)`
// chain). An inspector self-registers for a file extension via [AssetInspector(".mat")]; the panel asks this
// registry "what inspector draws extension X" and draws it. Adding a custom asset body = adding one
// [AssetInspector] class; InspectorPanel is never edited.
//
// Asset selection is PATH+EXTENSION-keyed (no single loaded asset object to switch on), so this resolves by
// the lower-cased file extension (".mat") rather than B1's component Type — the only structural difference
// from ComponentPreviewRegistry. A file has exactly ONE extension, so resolution returns ONE inspector (the
// best by priority/tie-key) rather than B1's list; an unregistered extension returns null and the panel draws
// only the file header (R1.9's never-blank fallback for assets — byte-identical to the switch's
// "// Everything else: just the file header — no clutter" default).
//
// Mirrors ComponentPreviewRegistry (B1) / EditorWindowRegistry (A1) limb-for-limb — the established
// self-register pattern:
//   - DISCOVERY via TypeCache.GetTypesWithAttribute<AssetInspectorAttribute>() (engine-side P0.1 substrate),
//     so inspectors in the host (editor) assembly are found by the same headless scan that finds [MenuItem]s,
//     [ComponentPreview]s, and game-script Behaviours, with zero editor-specific reflection.
//   - DETERMINISM (P0.4) via DeterministicResolver: should two inspectors ever claim the same extension, the
//     winner is HIGHER priority, then the inspector type's full name — a total, machine-independent function of
//     the registered set, never of assembly-load order.
//   - PERF (§4 / [[pref-no-reflection-render-hotpath]]): the scan + Activator.CreateInstance happen ONCE; the
//     per-extension resolved inspector is memoized. The inspector redraws every ImGui frame and pays ZERO
//     reflection after warm-up.
//   - HOT-RELOAD (P0.3): the caches self-register their invalidation into the central ReloadCaches contract via
//     the [ModuleInitializer] below — a game-script reload (a script may [AssetInspector] a custom asset type)
//     drops the cached inspectors + per-extension lookups; the next resolve re-discovers over the rebuilt
//     TypeCache.
internal static class AssetInspectorRegistry {
    // One discovered inspector: the extension it draws, its resolution priority, a stable tie-key, and the
    // (single, shared) instance that draws it. Inspectors are stateless shims — per-section state lives as
    // statics/fields on InspectorPanel — so one instance per registered (ext,type) pair is reused.
    readonly struct Entry {
        public Entry(string extension, int priority, string tieKey, IAssetInspector inspector) {
            Extension = extension;
            Priority = priority;
            TieKey = tieKey;
            Inspector = inspector;
        }
        public string Extension { get; }            // ".mat" — lower-case, leading dot (normalised by the attribute)
        public int Priority { get; }
        public string TieKey { get; }
        public IAssetInspector Inspector { get; }
    }

    static Entry[] discovered;                        // null = not yet scanned; Discover() fills it on first use
    // Memoized per-extension resolution: the winning inspector (or null). Cleared with `discovered` on
    // hot-reload. Extension (lower-case ".ext") -> the inspector that draws it, or null when none registered.
    static readonly Dictionary<string, IAssetInspector> perExtCache =
        new(StringComparer.OrdinalIgnoreCase);

    // The inspector that draws an asset extension, or null when none is registered (the panel then draws only
    // the file header — R1.9 fallback). The extension is matched case-insensitively but is expected lower-cased
    // (Path.GetExtension(path).ToLowerInvariant()) by the caller.
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

        IAssetInspector result = resolver.Resolve(_ => true);   // single best (highest priority, then tie-key)
        perExtCache[extension] = result;
        return result;
    }

    // Force a (re)scan now. Harnesses call this after a fresh TypeCache.Build; the editor lets the first
    // InspectorFor lazily trigger it (the first asset-inspector draw, never a frame the user notices).
    public static void Rebuild() {
        discovered = Discover();
        perExtCache.Clear();
    }

    static Entry[] Discover() {
        var entries = new List<Entry>();
        foreach (Type type in TypeCache.GetTypesWithAttribute<AssetInspectorAttribute>()) {
            // Defensive: TypeCache already filters to concrete+instantiable, but the attribute can sit on any
            // class — only IAssetInspector implementors are usable.
            if (!typeof(IAssetInspector).IsAssignableFrom(type))
                continue;

            IAssetInspector instance;
            try { instance = (IAssetInspector)Activator.CreateInstance(type); }
            catch { continue; }   // an inspector with a throwing ctor can't take the panel down

            // AllowMultiple: one inspector class may cover several extensions (e.g. .png/.jpg/.tga) — one
            // entry per attribute.
            foreach (AssetInspectorAttribute attr in type.GetCustomAttributes<AssetInspectorAttribute>()) {
                if (string.IsNullOrEmpty(attr.Extension))
                    continue;
                // Stable tie-break: extension + the inspector's own full name, so two inspectors sharing a
                // priority for the same extension still order identically on every machine.
                string tieKey = $"{attr.Extension} {type.FullName}";
                entries.Add(new Entry(attr.Extension, attr.Priority, tieKey, instance));
            }
        }
        return entries.ToArray();
    }

    // Hot-reload contract (P0.3): drop the discovered inspectors + per-extension lookups so the next resolve
    // re-scans over the rebuilt TypeCache. Self-registered via the [ModuleInitializer] below — joins TypeCache /
    // EditorWindowRegistry / ComponentPreviewRegistry / DrawerStackPlan in ReloadCaches; the reload site never
    // names this cache.
    static void ClearForReload() {
        discovered = null;
        perExtCache.Clear();
    }

    [ModuleInitializer]
    internal static void RegisterReloadInvalidation() => ReloadCaches.Register(ClearForReload);
}
