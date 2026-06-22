using System.Reflection;
using System.Text;

namespace BallisticEngine.Editor.Inspector;

public static class InspectorReflection {
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance;

    public static string Prettify(string name) {
        if (string.IsNullOrEmpty(name)) return name;
        var sb = new StringBuilder(name.Length + 4);
        sb.Append(char.ToUpperInvariant(name[0]));
        for (int i = 1; i < name.Length; i++) {
            if (char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])) sb.Append(' ');
            sb.Append(name[i]);
        }
        return sb.ToString();
    }

    public static object LogicalValue(object raw) {
        if (raw is null) return null;
        if (valueProp.TryGetValue(raw.GetType(), out PropertyInfo cached))
            return cached?.GetValue(raw) ?? raw;
        PropertyInfo vp = IsVolumeParameter(raw.GetType())
            ? raw.GetType().GetProperty("Value", Flags)
            : null;
        valueProp[raw.GetType()] = vp;
        return vp?.GetValue(raw) ?? raw;
    }

    static bool IsVolumeParameter(Type t) {
        for (Type b = t; b is not null; b = b.BaseType)
            if (b.Name == "VolumeParameter") return true;
        return false;
    }

    static readonly Dictionary<Type, PropertyInfo> valueProp = new();
    static readonly Dictionary<(Type, string), MemberInfo> siblingCache = new();

    public static bool TryGetSibling(object owner, string name, out object value) {
        value = null;
        if (owner is null || string.IsNullOrEmpty(name)) return false;
        Type type = owner.GetType();
        var key = (type, name);
        if (!siblingCache.TryGetValue(key, out MemberInfo member)) {
            member = (MemberInfo)type.GetProperty(name, Flags) ?? type.GetField(name, Flags);
            siblingCache[key] = member;
        }
        if (member is null) return false;
        object raw = member is PropertyInfo p ? p.GetValue(owner) : ((FieldInfo)member).GetValue(owner);
        value = LogicalValue(raw);
        return true;
    }
}
