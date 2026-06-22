using System.Reflection;
using System.Runtime.CompilerServices;

namespace BallisticEngine;

public static class TypeCache {
    static Type[] allTypes = [];

    static readonly Dictionary<Type, Type[]> derivedFromCache = new();
    static readonly Dictionary<Type, Type[]> typesWithAttrCache = new();
    static readonly Dictionary<Type, MethodInfo[]> methodsWithAttrCache = new();

    public static bool IsBuilt { get; private set; }

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

    public static void ClearForReload() {
        allTypes = [];
        derivedFromCache.Clear();
        typesWithAttrCache.Clear();
        methodsWithAttrCache.Clear();
        IsBuilt = false;
    }

    [ModuleInitializer]
    internal static void RegisterReloadInvalidation() => ReloadCaches.Register(ClearForReload);

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

    public static IReadOnlyList<MethodInfo> GetMethodsWithAttribute<TAttr>() where TAttr : Attribute =>
        GetMethodsWithAttribute(typeof(TAttr));

    public static IReadOnlyList<MethodInfo> GetMethodsWithAttribute(Type attributeType) {
        if (attributeType is null)
            return [];
        if (methodsWithAttrCache.TryGetValue(attributeType, out MethodInfo[] cached))
            return cached;

        var result = new List<MethodInfo>();
        foreach (Type type in allTypes) {
            if (type.ContainsGenericParameters)
                continue;
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

    static bool IsInstantiable(Type t) =>
        t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false } &&
        !t.ContainsGenericParameters &&
        t.GetConstructor(Type.EmptyTypes) is not null;

    static Type[] SafeGetTypes(Assembly asm) {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException e) {
            return e.Types.Where(t => t is not null).ToArray();
        }
    }
}
