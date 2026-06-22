using System.Reflection;

namespace BallisticEngine;

public static class BEventReflection {
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    static readonly Type[] PrimitiveArgTypes = {
        typeof(float), typeof(int), typeof(bool), typeof(string),
    };

    static readonly Dictionary<Type, HashSet<string>> VisibleFrameworkMethods = new() {
        [typeof(Entity)] = new(StringComparer.Ordinal) { nameof(Entity.SetActive) },
    };

    public static bool IsSupportedArgType(Type t) =>
        t is not null && (PrimitiveArgTypes.Contains(t) || t.IsEnum || typeof(BObject).IsAssignableFrom(t));

    public static IEnumerable<MethodInfo> InvokableMethods(Type targetType) {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (Type t = targetType; t is not null && t != typeof(object); t = t.BaseType) {
            foreach (MethodInfo m in t.GetMethods(Flags)) {
                if (!IsInvokable(m))
                    continue;
                if (IsHiddenFramework(m))
                    continue;
                string key = Signature(m);
                if (seen.Add(key))
                    yield return m;
            }
        }
    }

    static bool IsInvokable(MethodInfo m) {
        if (m.IsSpecialName || m.IsGenericMethod || m.ReturnType != typeof(void))
            return false;
        ParameterInfo[] ps = m.GetParameters();
        if (ps.Length == 0)
            return true;
        return ps.Length == 1 && IsSupportedArgType(ps[0].ParameterType);
    }

    static bool IsHiddenFramework(MethodInfo m) {
        Type dt = m.DeclaringType;
        bool isFramework = dt == typeof(BObject) || dt == typeof(Component) ||
                           dt == typeof(Behaviour) || dt == typeof(SceneBehaviour) ||
                           dt == typeof(Renderer) || dt == typeof(Entity);
        if (!isFramework)
            return false;
        return !(VisibleFrameworkMethods.TryGetValue(dt, out HashSet<string> allowed) && allowed.Contains(m.Name));
    }

    static string Signature(MethodInfo m) =>
        $"{m.Name}({string.Join(',', m.GetParameters().Select(p => p.ParameterType.FullName))})";
}
