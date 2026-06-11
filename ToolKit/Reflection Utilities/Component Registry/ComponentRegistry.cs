using System.Reflection;

namespace BallisticEngine;

public readonly record struct ComponentEntry(string DisplayName, string Menu, Type Type);

// Discovers all concrete Behaviour types by reflection (mirrors SingleServiceInstaller's pattern)
// so scene deserialization can resolve a component by name and the editor can list them in an
// Add Component menu. Built once at startup from EngineBootstrap.
public static class ComponentRegistry {
    static readonly Dictionary<string, Type> byName = new(StringComparer.Ordinal);
    static readonly List<ComponentEntry> menu = new();

    // SceneBehaviours live in a separate registry: they go on the Scene (the editor's
    // "Scene" hierarchy), never in the entity Add Component menu.
    static readonly Dictionary<string, Type> sceneByName = new(StringComparer.Ordinal);
    static readonly List<ComponentEntry> sceneMenu = new();

    public static IReadOnlyDictionary<string, Type> ByName => byName;
    public static IReadOnlyList<ComponentEntry> Menu => menu;
    public static IReadOnlyList<ComponentEntry> SceneMenu => sceneMenu;

    public static void Build(params Assembly[] assemblies) {
        byName.Clear();
        menu.Clear();
        sceneByName.Clear();
        sceneMenu.Clear();

        foreach (Assembly assembly in assemblies.Distinct()) {
            foreach (Type type in assembly.GetTypes()) {
                if (type.IsAbstract || !type.IsPublic)
                    continue;
                if (type.GetConstructor(Type.EmptyTypes) is null)
                    continue;

                if (typeof(Behaviour).IsAssignableFrom(type))
                    Register(type, byName, menu);
                else if (typeof(SceneBehaviour).IsAssignableFrom(type))
                    Register(type, sceneByName, sceneMenu);
            }
        }

        menu.Sort((a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName));
        sceneMenu.Sort((a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName));
    }

    static void Register(Type type, Dictionary<string, Type> names, List<ComponentEntry> entries) {
        // Keyed by simple name (FullName disambiguates collisions).
        var key = names.ContainsKey(type.Name) ? type.FullName : type.Name;
        names[key] = type;

        ComponentAttribute attr = type.GetCustomAttribute<ComponentAttribute>();
        entries.Add(new ComponentEntry(
            attr?.DisplayName ?? type.Name,
            attr?.Menu ?? string.Empty,
            type));
    }

    public static Type Resolve(string typeName) =>
        typeName is null ? null : byName.GetValueOrDefault(typeName);

    public static Type ResolveScene(string typeName) =>
        typeName is null ? null : sceneByName.GetValueOrDefault(typeName);

    // Returns the registry key (the name used in scene files) for a component instance.
    public static string NameOf(Behaviour behaviour) {
        Type type = behaviour.GetType();
        return byName.ContainsKey(type.Name) && byName[type.Name] == type ? type.Name : type.FullName;
    }

    public static string SceneNameOf(SceneBehaviour behaviour) {
        Type type = behaviour.GetType();
        return sceneByName.ContainsKey(type.Name) && sceneByName[type.Name] == type ? type.Name : type.FullName;
    }
}
