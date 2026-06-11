using System.Reflection;

namespace BallisticEngine;

// Which methods a BEvent persistent listener can call, and which argument types it can pass —
// shared by the inspector (to populate the method dropdown / arg widget) and PersistentListener
// (to bind at invoke time), so the two never disagree about what's wireable.
//
// Unity's rule, adapted: public instance methods returning void, taking either no parameter or a
// single parameter of a serializable type (the primitives the scene serializer already round-trips,
// plus any BObject for asset/scene-object refs). Framework plumbing declared on the BObject/Component/
// Behaviour/Entity base types is hidden so the dropdown shows the component's OWN methods first —
// except a small, useful allow-list (Entity.SetActive, Behaviour enable toggles) kept visible
// because they are exactly what events are most often wired to (Unity surfaces these too).
public static class BEventReflection {
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    // Argument types a static/dynamic listener may use. BObject is handled separately (any subclass
    // qualifies and serializes as a guid ref).
    static readonly Type[] PrimitiveArgTypes = {
        typeof(float), typeof(int), typeof(bool), typeof(string),
    };

    // Base-type methods that stay visible despite living on framework types (Unity does the same for
    // GameObject.SetActive etc.). Keyed by declaring type -> method names.
    static readonly Dictionary<Type, HashSet<string>> VisibleFrameworkMethods = new() {
        [typeof(Entity)] = new(StringComparer.Ordinal) { nameof(Entity.SetActive) },
    };

    public static bool IsSupportedArgType(Type t) =>
        t is not null && (PrimitiveArgTypes.Contains(t) || t.IsEnum || typeof(BObject).IsAssignableFrom(t));

    // Every method on `targetType` that a listener could call (any supported signature). Walks the
    // whole hierarchy so a component's inherited-but-own methods show, then filters framework noise.
    public static IEnumerable<MethodInfo> InvokableMethods(Type targetType) {
        var seen = new HashSet<string>(StringComparer.Ordinal); // dedupe overrides/news by name+sig
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
            return false; // skip property accessors, operators, generics, non-void
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
