using System.Reflection;
using System.Runtime.CompilerServices;

namespace BallisticEngine;

// The shared reflection substrate (editor-rework plan, Rule 1.75 / P0.1). GENERALIZES
// ComponentRegistry's bootstrap scan ("all concrete Behaviour/SceneBehaviour/... types") into
// arbitrary type/attribute queries that every self-registering registry can ask:
//
//   - "all concrete types deriving from T"           → [SerializeReference] dropdown (G3),
//                                                        component/asset preview registries (B1/B2)
//   - "all (concrete) types carrying attribute A"     → attribute-drawer discovery (B0)
//   - "all static methods carrying attribute A"        → [MenuItem] window discovery (A1)
//
// Lives in the ENGINE (not the editor): the serializer (Engine/Serialization) needs the derived-type
// query to resolve [SerializeReference] $type tags HEADLESSLY (bal/runtime have no editor). Same
// lifecycle as ComponentRegistry — ONE scan at bootstrap (EngineBootstrap.BuildComponentRegistry),
// rebuilt from scratch on ALC hot-reload.
//
// HOT-RELOAD (P0.3, Chunk 3): TypeCache self-registers ClearForReload into the central ReloadCaches
// contract via the [ModuleInitializer] below, so EngineBootstrap.ReloadGameScripts drops the stale type
// snapshot at the reload boundary (before GameScripts.Unload), alongside the InputRegistry / Network
// registries. Build() still fully replaces the snapshot on the rebuild that follows; the explicit clear
// makes invalidation a FORMAL contract instead of relying on the reload path happening to re-invoke
// Build() — and guarantees no stale game-script Type survives even momentarily between unload and rebuild.
//
// PERF (editor-rework §4): queries are computed ONCE per (T / attribute) at first ask and cached, so
// per-frame UI code (the inspector redraws every frame in ImGui) pays zero reflection — [[pref-no-
// reflection-render-hotpath]] applied to the editor. Build() clears the result caches too, so a
// hot-reload can't serve stale derived-type lists.
public static class TypeCache {
    // The flat snapshot of every loaded type, taken once in Build(). All queries scan THIS, never
    // re-call Assembly.GetTypes() (which re-throws ReflectionTypeLoadException + reallocates).
    static Type[] allTypes = [];

    // Memoized query results, keyed so repeated asks are O(1). Cleared on every Build().
    static readonly Dictionary<Type, Type[]> derivedFromCache = new();
    static readonly Dictionary<Type, Type[]> typesWithAttrCache = new();
    static readonly Dictionary<Type, MethodInfo[]> methodsWithAttrCache = new();

    // True once Build() has run. Queries before Build() return empty (they'd be empty anyway, but the
    // flag lets a registry assert it was initialized in order).
    public static bool IsBuilt { get; private set; }

    // Scan the given assemblies ONCE, snapshot every type, and reset the query caches. Called from
    // EngineBootstrap.BuildComponentRegistry with the same assembly set as ComponentRegistry.Build
    // (engine + host + game scripts), at bootstrap AND on hot-reload — a fresh Build() fully replaces
    // the snapshot so reloaded game-script types appear and unloaded ones vanish.
    public static void Build(params Assembly[] assemblies) {
        var types = new List<Type>();
        foreach (Assembly asm in assemblies.Where(a => a is not null).Distinct())
            types.AddRange(SafeGetTypes(asm));

        allTypes = types.ToArray();
        derivedFromCache.Clear();
        typesWithAttrCache.Clear();
        methodsWithAttrCache.Clear();
        IsBuilt = true;
    }

    // P0.3 hot-reload invalidation: drop the type snapshot AND every memoized query so no Type from the
    // unloaded game-script ALC survives. Self-registered into ReloadCaches (the [ModuleInitializer] below)
    // and invoked by EngineBootstrap.ReloadGameScripts before GameScripts.Unload; Build() then re-scans the
    // new assembly set. IsBuilt drops to false so a query in the brief unbuilt window returns empty (safe)
    // rather than serving a stale list. The clear is idempotent.
    public static void ClearForReload() {
        allTypes = [];
        derivedFromCache.Clear();
        typesWithAttrCache.Clear();
        methodsWithAttrCache.Clear();
        IsBuilt = false;
    }

    // THE one-line rule (P0.3): TypeCache wires its own invalidation into the central reload contract at
    // assembly load — a [ModuleInitializer] (not a lazy static ctor) so it is registered before any reload
    // can occur, even if no code has touched TypeCache yet. Mirrors the source-gen [ModuleInitializer]
    // registration the network registries use.
    [ModuleInitializer]
    internal static void RegisterReloadInvalidation() => ReloadCaches.Register(ClearForReload);

    // All CONCRETE, INSTANTIABLE types assignable to T (T may be an interface, an abstract class, or a
    // concrete base). "Instantiable" = the [SerializeReference]/Add-Component contract: a closed,
    // non-abstract type with a public parameterless constructor — exactly what the inspector dropdown
    // can `new` and edit. T itself is excluded when it is abstract/interface; included if it is a
    // concrete instantiable base. Results are deterministically ordered (by full name) so a dropdown is
    // stable across machines/builds, and cached per T.
    public static IReadOnlyList<Type> GetTypesDerivedFrom<T>() => GetTypesDerivedFrom(typeof(T));

    public static IReadOnlyList<Type> GetTypesDerivedFrom(Type baseType) {
        if (baseType is null)
            return [];
        if (derivedFromCache.TryGetValue(baseType, out Type[] cached))
            return cached;

        Type[] result = allTypes
            .Where(t => baseType.IsAssignableFrom(t) && IsInstantiable(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToArray();
        derivedFromCache[baseType] = result;
        return result;
    }

    // All CONCRETE, INSTANTIABLE types carrying attribute TAttr (inherited attributes included). Used
    // for attribute-drawer discovery (B0): a drawer type marks itself, e.g. [DrawerFor(typeof(Foo))].
    // Concrete+instantiable because the consumer Activator.CreateInstances it; if a future query needs
    // attribute-carrying ABSTRACT types, add a separate overload rather than loosening this one.
    public static IReadOnlyList<Type> GetTypesWithAttribute<TAttr>() where TAttr : Attribute =>
        GetTypesWithAttribute(typeof(TAttr));

    public static IReadOnlyList<Type> GetTypesWithAttribute(Type attributeType) {
        if (attributeType is null)
            return [];
        if (typesWithAttrCache.TryGetValue(attributeType, out Type[] cached))
            return cached;

        Type[] result = allTypes
            .Where(t => IsInstantiable(t) && t.IsDefined(attributeType, inherit: true))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToArray();
        typesWithAttrCache[attributeType] = result;
        return result;
    }

    // All PUBLIC STATIC methods carrying attribute TAttr, across every scanned type. This is the
    // [MenuItem("Tools/...")] window-discovery query (A1): a window self-registers via a static
    // parameterless method marked with the attribute; the shell reflection-scans these at bootstrap to
    // populate the menu bar. Methods are returned in a deterministic order (declaring type's full name,
    // then method name) so menu population is stable.
    public static IReadOnlyList<MethodInfo> GetMethodsWithAttribute<TAttr>() where TAttr : Attribute =>
        GetMethodsWithAttribute(typeof(TAttr));

    public static IReadOnlyList<MethodInfo> GetMethodsWithAttribute(Type attributeType) {
        if (attributeType is null)
            return [];
        if (methodsWithAttrCache.TryGetValue(attributeType, out MethodInfo[] cached))
            return cached;

        var result = new List<MethodInfo>();
        foreach (Type type in allTypes) {
            // Open generic declaring types can't host an invocable static menu method; skip them so the
            // returned MethodInfos are always directly invokable.
            if (type.ContainsGenericParameters)
                continue;
            // Include NonPublic: Unity-style [MenuItem] handler methods are conventionally `private static`
            // (written without an access modifier, e.g. `[MenuItem("Window/Profiler")] static void Profiler()`).
            // Without NonPublic the whole Window menu came up EMPTY — the discovery scan matched zero methods.
            MethodInfo[] methods = type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
            foreach (MethodInfo method in methods)
                if (method.IsDefined(attributeType, inherit: true))
                    result.Add(method);
        }

        MethodInfo[] ordered = result
            .OrderBy(m => m.DeclaringType?.FullName, StringComparer.Ordinal)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .ToArray();
        methodsWithAttrCache[attributeType] = ordered;
        return ordered;
    }

    // The polymorphic-scope rule (Rule 1.75, Unity/Odin parity): a type is a valid [SerializeReference]
    // / Add-Component candidate iff it is CONCRETE and CLOSED-GENERIC and has a PUBLIC PARAMETERLESS
    // ctor. Excludes: abstract classes, interfaces, open generics (T unbound — an unsupported field
    // type), and types with no public default ctor (can't be `new`'d by the dropdown). Mirrors
    // ComponentRegistry's own `IsAbstract || !IsPublic || no-ctor` filter, plus the open-generic guard
    // ComponentRegistry didn't need.
    static bool IsInstantiable(Type t) =>
        t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false } &&
        !t.ContainsGenericParameters &&
        t.GetConstructor(Type.EmptyTypes) is not null;

    // Robust against a partially-loadable assembly (a missing transitive dependency throws on
    // GetTypes()): keep the types that DID load, exactly like InputRegistry.ScanForActions.
    static Type[] SafeGetTypes(Assembly asm) {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException e) {
            return e.Types.Where(t => t is not null).ToArray();
        }
    }
}
