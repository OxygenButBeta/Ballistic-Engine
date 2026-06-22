using System.Reflection;

namespace BallisticEngine.UI;

sealed class PropertyAccessor
{
    readonly PropertyInfo _prop;
    readonly FieldInfo _field;
    readonly PropertyAccessor _next;

    PropertyAccessor(PropertyInfo p, FieldInfo f, PropertyAccessor next) { _prop = p; _field = f; _next = next; }

    public bool CanSet => _next != null ? _next.CanSet : (_prop?.CanWrite ?? _field != null);

    public static PropertyAccessor For(Type type, string path)
    {
        int dot = path.IndexOf('.');
        string head = dot < 0 ? path : path[..dot];
        var prop = type.GetProperty(head, BindingFlags.Public | BindingFlags.Instance);
        var field = prop == null ? type.GetField(head, BindingFlags.Public | BindingFlags.Instance) : null;
        if (prop == null && field == null) return null;
        Type memberType = prop?.PropertyType ?? field.FieldType;
        PropertyAccessor next = dot < 0 ? null : For(memberType, path[(dot + 1)..]);
        if (dot >= 0 && next == null) return null;
        return new PropertyAccessor(prop, field, next);
    }

    object GetRaw(object obj) => _prop != null ? _prop.GetValue(obj) : _field.GetValue(obj);

    public bool TryGet(object obj, out object value)
    {
        value = null;
        if (obj == null) return false;
        object raw = GetRaw(obj);
        if (_next == null) { value = raw; return true; }
        return _next.TryGet(raw, out value);
    }

    public void Set(object obj, object value)
    {
        if (obj == null) return;
        if (_next != null) { _next.Set(GetRaw(obj), value); return; }
        if (_prop != null && _prop.CanWrite) _prop.SetValue(obj, value);
        else _field?.SetValue(obj, value);
    }
}
