using System.Reflection;

namespace BallisticEngine.Serialization;

// Shared rule for "which members of a component are user-editable state": public read/write
// properties and public mutable fields, excluding framework plumbing declared on the base
// classes. Used by both the scene serializer and the editor inspector so they agree.
public static class ComponentReflection {
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance;
    // Field scan also pulls NON-public fields so a private/protected field marked [SerializeField] (Unity
    // parity) can opt into serialization; unmarked non-public fields are filtered out in SerializableMembers.
    const BindingFlags FieldFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    static bool IsFrameworkType(Type declaringType) =>
        declaringType == typeof(BObject) ||
        declaringType == typeof(Component) ||
        declaringType == typeof(Behaviour) ||
        declaringType == typeof(SceneBehaviour) ||
        declaringType == typeof(Renderer) ||
        declaringType == typeof(DataAsset);

    public static IEnumerable<MemberInfo> SerializableMembers(Type type) {
        foreach (PropertyInfo prop in type.GetProperties(Flags)) {
            if (prop.CanRead && prop.CanWrite && prop.GetIndexParameters().Length == 0 &&
                !IsFrameworkType(prop.DeclaringType) &&
                prop.GetCustomAttribute<NotSerializedAttribute>() is null)
                yield return prop;
        }
        foreach (FieldInfo field in type.GetFields(FieldFlags)) {
            if (field.IsLiteral || IsFrameworkType(field.DeclaringType) ||
                field.GetCustomAttribute<NotSerializedAttribute>() is not null)
                continue;
            // A NON-PUBLIC field is serializable state ONLY when it opts in with [SerializeField] (Unity
            // parity). This also filters out compiler-generated members the NonPublic scan now sees —
            // auto-property backing fields (`<Prop>k__BackingField`), captured locals, etc. — none of which
            // carry [SerializeField], so they're skipped without an explicit name check. Public fields are
            // unchanged (no marker needed), so every existing scene/component is byte-identical.
            if (!field.IsPublic && field.GetCustomAttribute<SerializeFieldAttribute>() is null)
                continue;
            // `readonly` fields are normally skipped (their value can't change), EXCEPT BEvents: they're
            // declared `public readonly BEvent OnX = new();` and populated IN PLACE (listeners added to
            // the existing instance, never reassigned), so they must still serialize + show in the
            // inspector. Without this carve-out a readonly BEvent field is invisible.
            if (field.IsInitOnly && !typeof(BEvent).IsAssignableFrom(field.FieldType))
                continue;
            yield return field;
        }
    }

    // The members the editor inspector should show: the serializable set minus anything marked
    // [HideInInspector]. Kept SEPARATE from SerializableMembers on purpose — hiding a member from
    // the inspector must not drop it from save/load.
    public static IEnumerable<MemberInfo> InspectorMembers(Type type) {
        foreach (MemberInfo member in SerializableMembers(type)) {
            if (member.GetCustomAttribute<HideInInspectorAttribute>() is null)
                yield return member;
        }
    }

    // Parameterless methods marked [Button]: the inspector renders each as a clickable button
    // that invokes the method on the component (bake triggers, one-shot actions).
    public static IEnumerable<MethodInfo> InspectorButtons(Type type) {
        foreach (MethodInfo method in type.GetMethods(Flags)) {
            if (method.GetParameters().Length == 0 &&
                !IsFrameworkType(method.DeclaringType) &&
                method.GetCustomAttribute<ButtonAttribute>() is not null)
                yield return method;
        }
    }

    // Parameterless methods marked [ContextMenu]: the inspector lists each in the component's "..."
    // context menu and invokes it on click (Unity's [ContextMenu]).
    public static IEnumerable<MethodInfo> InspectorContextMenus(Type type) {
        foreach (MethodInfo method in type.GetMethods(Flags)) {
            if (method.GetParameters().Length == 0 &&
                !IsFrameworkType(method.DeclaringType) &&
                method.GetCustomAttribute<ContextMenuAttribute>() is not null)
                yield return method;
        }
    }

    // Parameterless methods marked [EditorWindowExecutionPoint]: the inspector renders a window-open
    // button that invokes the method and opens a dedicated EditorWindow for the component.
    public static IEnumerable<MethodInfo> InspectorWindowPoints(Type type) {
        foreach (MethodInfo method in type.GetMethods(Flags)) {
            if (method.GetParameters().Length == 0 &&
                !IsFrameworkType(method.DeclaringType) &&
                method.GetCustomAttribute<EditorWindowExecutionPointAttribute>() is not null)
                yield return method;
        }
    }

    public static Type MemberType(MemberInfo member) =>
        member is PropertyInfo p ? p.PropertyType : ((FieldInfo)member).FieldType;

    public static object GetValue(MemberInfo member, object target) =>
        member is PropertyInfo p ? p.GetValue(target) : ((FieldInfo)member).GetValue(target);

    public static void SetValue(MemberInfo member, object target, object value) {
        if (member is PropertyInfo p)
            p.SetValue(target, value);
        else
            ((FieldInfo)member).SetValue(target, value);
    }
}
