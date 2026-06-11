using System.Reflection;

namespace BallisticEngine.Serialization;

// Shared rule for "which members of a component are user-editable state": public read/write
// properties and public mutable fields, excluding framework plumbing declared on the base
// classes. Used by both the scene serializer and the editor inspector so they agree.
public static class ComponentReflection {
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance;

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
        foreach (FieldInfo field in type.GetFields(Flags)) {
            if (!field.IsInitOnly && !field.IsLiteral && !IsFrameworkType(field.DeclaringType) &&
                field.GetCustomAttribute<NotSerializedAttribute>() is null)
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
