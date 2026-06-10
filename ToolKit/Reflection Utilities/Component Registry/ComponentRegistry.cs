using System.Reflection;

namespace BallisticEngine;

public readonly record struct ComponentEntry(string DisplayName, string Menu, Type Type);

// Discovers all concrete Behaviour types by reflection (mirrors SingleServiceInstaller's pattern)
// so scene deserialization can resolve a component by name and the editor can list them in an
// Add Component menu. Built once at startup from EngineBootstrap.
public static class ComponentRegistry {
    static readonly Dictionary<string, Type> byName = new(StringComparer.Ordinal);
    static readonly List<ComponentEntry> menu = new();

    public static IReadOnlyDictionary<string, Type> ByName => byName;
    public static IReadOnlyList<ComponentEntry> Menu => menu;

    public static void Build(params Assembly[] assemblies) {
        byName.Clear();
        menu.Clear();

        foreach (Assembly assembly in assemblies.Distinct()) {
            foreach (Type type in assembly.GetTypes()) {
                if (type.IsAbstract || !type.IsPublic || !typeof(Behaviour).IsAssignableFrom(type))
                    continue;
                if (type.GetConstructor(Type.EmptyTypes) is null)
                    continue;

                // Keyed by simple name (FullName disambiguates collisions).
                var key = byName.ContainsKey(type.Name) ? type.FullName : type.Name;
                byName[key] = type;

                ComponentAttribute attr = type.GetCustomAttribute<ComponentAttribute>();
                menu.Add(new ComponentEntry(
                    attr?.DisplayName ?? type.Name,
                    attr?.Menu ?? string.Empty,
                    type));
            }
        }

        menu.Sort((a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName));
    }

    public static Type Resolve(string typeName) {
        if (typeName is null)
            return null;
        return byName.GetValueOrDefault(typeName);
    }

    // Returns the registry key (the name used in scene files) for a component instance.
    public static string NameOf(Behaviour behaviour) {
        Type type = behaviour.GetType();
        return byName.ContainsKey(type.Name) && byName[type.Name] == type ? type.Name : type.FullName;
    }
}
