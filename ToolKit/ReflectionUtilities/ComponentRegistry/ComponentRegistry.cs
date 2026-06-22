using System.Reflection;

namespace BallisticEngine;

public static class ComponentRegistry {
    static readonly Dictionary<string, Type> byName = new(StringComparer.Ordinal);
    static readonly List<ComponentEntry> menu = new();

    static readonly Dictionary<string, Type> sceneByName = new(StringComparer.Ordinal);
    static readonly List<ComponentEntry> sceneMenu = new();

    static readonly Dictionary<string, Type> volumeByName = new(StringComparer.Ordinal);
    static readonly List<ComponentEntry> volumeMenu = new();

    static readonly Dictionary<string, Type> featureByName = new(StringComparer.Ordinal);
    static readonly List<ComponentEntry> featureMenu = new();

    static readonly Dictionary<string, Type> dataByName = new(StringComparer.Ordinal);
    static readonly List<ComponentEntry> dataMenu = new();

    public static IReadOnlyDictionary<string, Type> ByName => byName;
    public static IReadOnlyList<ComponentEntry> Menu => menu;
    public static IReadOnlyList<ComponentEntry> SceneMenu => sceneMenu;
    public static IReadOnlyList<ComponentEntry> VolumeMenu => volumeMenu;
    public static IReadOnlyList<ComponentEntry> RenderFeatureMenu => featureMenu;
    public static IReadOnlyList<ComponentEntry> DataAssetMenu => dataMenu;

    public static void Build(params Assembly[] assemblies) {
        byName.Clear();
        menu.Clear();
        sceneByName.Clear();
        sceneMenu.Clear();
        volumeByName.Clear();
        volumeMenu.Clear();
        featureByName.Clear();
        featureMenu.Clear();
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
                else if (typeof(RenderFeature).IsAssignableFrom(type))
                    RegisterFeature(type);
                else if (typeof(DataAsset).IsAssignableFrom(type))
                    RegisterDataAsset(type);
            }
        }

        menu.Sort((a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName));
        sceneMenu.Sort((a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName));
        volumeMenu.Sort((a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName));
        featureMenu.Sort((a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName));
        dataMenu.Sort((a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName));
    }

    static void RegisterFeature(Type type) {
        var key = featureByName.ContainsKey(type.Name) ? type.FullName : type.Name;
        featureByName[key] = type;

        RenderFeatureAttribute attr = type.GetCustomAttribute<RenderFeatureAttribute>();
        if (attr is { HideFromAddMenu: true })
            return;
        featureMenu.Add(new ComponentEntry(
            attr?.DisplayName ?? type.Name,
            attr?.Menu ?? string.Empty,
            type));
    }

    static void RegisterDataAsset(Type type) {
        var key = dataByName.ContainsKey(type.Name) ? type.FullName : type.Name;
        dataByName[key] = type;

        CreateDataAssetAttribute attr = type.GetCustomAttribute<CreateDataAssetAttribute>();
        if (attr is not null)
            dataMenu.Add(new ComponentEntry(attr.DisplayName ?? type.Name, attr.Menu ?? string.Empty, type));
    }

    public static Type ResolveDataAsset(string typeName) =>
        typeName is null ? null : dataByName.GetValueOrDefault(typeName);

    public static string DataAssetNameOf(Type type) =>
        dataByName.ContainsKey(type.Name) && dataByName[type.Name] == type ? type.Name : type.FullName;

    static void Register(Type type, Dictionary<string, Type> names, List<ComponentEntry> entries) {
        var key = names.ContainsKey(type.Name) ? type.FullName : type.Name;
        names[key] = type;

        ComponentAttribute attr = type.GetCustomAttribute<ComponentAttribute>();
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

    public static Type ResolveFeature(string typeName) =>
        typeName is null ? null : featureByName.GetValueOrDefault(typeName);

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

    public static string FeatureNameOf(RenderFeature feature) {
        Type type = feature.GetType();
        return featureByName.ContainsKey(type.Name) && featureByName[type.Name] == type ? type.Name : type.FullName;
    }
}
