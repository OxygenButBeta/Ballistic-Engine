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

    // VolumeComponents (post-process overrides) — resolved by name when loading .volume
    // profiles and listed in the editor's Add Override menu.
    static readonly Dictionary<string, Type> volumeByName = new(StringComparer.Ordinal);
    static readonly List<ComponentEntry> volumeMenu = new();

    // DataAssets (.asset files) — resolved by name when loading a .asset and listed in the asset
    // browser's "Create" menu (only types carrying [CreateDataAsset] appear in the menu; ALL
    // concrete DataAsset types are resolvable by name for loading).
    static readonly Dictionary<string, Type> dataByName = new(StringComparer.Ordinal);
    static readonly List<ComponentEntry> dataMenu = new();

    public static IReadOnlyDictionary<string, Type> ByName => byName;
    public static IReadOnlyList<ComponentEntry> Menu => menu;
    public static IReadOnlyList<ComponentEntry> SceneMenu => sceneMenu;
    public static IReadOnlyList<ComponentEntry> VolumeMenu => volumeMenu;
    public static IReadOnlyList<ComponentEntry> DataAssetMenu => dataMenu;

    public static void Build(params Assembly[] assemblies) {
        byName.Clear();
        menu.Clear();
        sceneByName.Clear();
        sceneMenu.Clear();
        volumeByName.Clear();
        volumeMenu.Clear();
        dataByName.Clear();
        dataMenu.Clear();

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
                else if (typeof(VolumeComponent).IsAssignableFrom(type))
                    Register(type, volumeByName, volumeMenu);
                else if (typeof(DataAsset).IsAssignableFrom(type))
                    RegisterDataAsset(type);
            }
        }

        menu.Sort((a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName));
        sceneMenu.Sort((a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName));
        volumeMenu.Sort((a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName));
        dataMenu.Sort((a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName));
    }

    // DataAssets are always resolvable by name (for loading), but only those carrying
    // [CreateDataAsset] surface in the browser's Create menu.
    static void RegisterDataAsset(Type type) {
        var key = dataByName.ContainsKey(type.Name) ? type.FullName : type.Name;
        dataByName[key] = type;

        CreateDataAssetAttribute attr = type.GetCustomAttribute<CreateDataAssetAttribute>();
        if (attr is not null)
            dataMenu.Add(new ComponentEntry(attr.DisplayName ?? type.Name, attr.Menu ?? string.Empty, type));
    }

    public static Type ResolveDataAsset(string typeName) =>
        typeName is null ? null : dataByName.GetValueOrDefault(typeName);

    // The registry key (the name stored in a .asset file) for a DataAsset type.
    public static string DataAssetNameOf(Type type) =>
        dataByName.ContainsKey(type.Name) && dataByName[type.Name] == type ? type.Name : type.FullName;

    static void Register(Type type, Dictionary<string, Type> names, List<ComponentEntry> entries) {
        // Keyed by simple name (FullName disambiguates collisions).
        var key = names.ContainsKey(type.Name) ? type.FullName : type.Name;
        names[key] = type;

        ComponentAttribute attr = type.GetCustomAttribute<ComponentAttribute>();
        // HideFromAddMenu: keep the name->type mapping (above) so existing scenes still deserialize and
        // the renderer can resolve it, but DON'T list it in the Add-Component menu — it's automatic now.
        if (attr is { HideFromAddMenu: true })
            return;
        entries.Add(new ComponentEntry(
            attr?.DisplayName ?? type.Name,
            attr?.Menu ?? string.Empty,
            type));
    }

    public static Type Resolve(string typeName) =>
        typeName is null ? null : byName.GetValueOrDefault(typeName);

    public static Type ResolveScene(string typeName) =>
        typeName is null ? null : sceneByName.GetValueOrDefault(typeName);

    public static Type ResolveVolume(string typeName) =>
        typeName is null ? null : volumeByName.GetValueOrDefault(typeName);

    // Returns the registry key (the name used in scene files) for a component instance.
    public static string NameOf(Behaviour behaviour) {
        Type type = behaviour.GetType();
        return byName.ContainsKey(type.Name) && byName[type.Name] == type ? type.Name : type.FullName;
    }

    public static string SceneNameOf(SceneBehaviour behaviour) {
        Type type = behaviour.GetType();
        return sceneByName.ContainsKey(type.Name) && sceneByName[type.Name] == type ? type.Name : type.FullName;
    }

    public static string VolumeNameOf(VolumeComponent component) {
        Type type = component.GetType();
        return volumeByName.ContainsKey(type.Name) && volumeByName[type.Name] == type ? type.Name : type.FullName;
    }
}
