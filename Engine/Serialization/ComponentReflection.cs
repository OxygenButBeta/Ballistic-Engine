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
        declaringType == typeof(Renderer);

    public static IEnumerable<MemberInfo> SerializableMembers(Type type) {
        foreach (PropertyInfo prop in type.GetProperties(Flags)) {
            if (prop.CanRead && prop.CanWrite && prop.GetIndexParameters().Length == 0 &&
                !IsFrameworkType(prop.DeclaringType))
                yield return prop;
        }
        foreach (FieldInfo field in type.GetFields(Flags)) {
            if (!field.IsInitOnly && !field.IsLiteral && !IsFrameworkType(field.DeclaringType))
                yield return field;
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
