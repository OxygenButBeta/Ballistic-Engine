using System.Reflection;

namespace BallisticEngine.Serialization;

public static class ComponentReflection {
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance;

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
            if (!field.IsPublic && field.GetCustomAttribute<SerializeFieldAttribute>() is null)
                continue;
            if (field.IsInitOnly && !typeof(BEvent).IsAssignableFrom(field.FieldType))
                continue;
            yield return field;
        }
    }

    public static IEnumerable<MemberInfo> InspectorMembers(Type type) {
        foreach (MemberInfo member in SerializableMembers(type)) {
            if (member.GetCustomAttribute<HideInInspectorAttribute>() is null)
                yield return member;
        }
    }

    public static IEnumerable<MethodInfo> InspectorButtons(Type type) {
        foreach (MethodInfo method in type.GetMethods(Flags)) {
            if (method.GetParameters().Length == 0 &&
                !IsFrameworkType(method.DeclaringType) &&
                method.GetCustomAttribute<ButtonAttribute>() is not null)
                yield return method;
        }
    }

    public static IEnumerable<MethodInfo> InspectorContextMenus(Type type) {
        foreach (MethodInfo method in type.GetMethods(Flags)) {
            if (method.GetParameters().Length == 0 &&
                !IsFrameworkType(method.DeclaringType) &&
                method.GetCustomAttribute<ContextMenuAttribute>() is not null)
                yield return method;
        }
    }

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
