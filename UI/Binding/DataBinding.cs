using System;
using System.Reflection;

namespace BallisticEngine.UI;

// Data-binding (P7.2) — wires an INotifyValueChanged<T> control to a property on a data source, one-way
// or two-way, so a control reflects + edits backing game state without hand-wiring every callback (UITK
// parity: binding-path). Reflection resolves the property ONCE at bind time (cached MemberInfo), not per
// frame, so it stays off the audit's "no reflection in the hot path" rule — binding is a setup-time act.
//
//   Binding.Bind(toggle, settings, nameof(Settings.Muted));            // two-way
//   Binding.Bind(label,  player,   nameof(Player.Name), oneWay:true);  // one-way (display)
//
// For a plain Label (no INotifyValueChanged<string>) use BindText.
public static class Binding
{
    // Two-way bind a value control to source.<path>. Pushes the current source value into the control,
    // then keeps them in sync: control change -> source set; (if the source raises changes) you can call
    // Refresh to pull. Returns an IDisposable to unbind.
    public static IDisposable Bind<T>(INotifyValueChanged<T> control, object source, string path, bool oneWay = false)
    {
        if (control == null || source == null || string.IsNullOrEmpty(path))
            return Noop.Instance;

        var accessor = PropertyAccessor.For(source.GetType(), path);
        if (accessor == null) return Noop.Instance;

        // initial pull: source -> control (silent so we don't echo back)
        if (accessor.TryGet(source, out object v) && v is T tv)
            control.SetValueWithoutNotify(tv);

        Action<T, T> onChange = null;
        if (!oneWay && accessor.CanSet)
        {
            onChange = (_, nv) => accessor.Set(source, nv);
            control.ValueChanged += onChange;
        }
        return new Unbinder<T>(control, onChange);
    }

    // One-way bind a Label's text to source.<path> (ToString of the value). Pull via Refresh.
    public static LabelBinding BindText(Label label, object source, string path)
    {
        var accessor = PropertyAccessor.For(source.GetType(), path);
        var b = new LabelBinding(label, source, accessor);
        b.Refresh();
        return b;
    }

    public sealed class LabelBinding
    {
        readonly Label _label; readonly object _source; readonly PropertyAccessor _acc;
        internal LabelBinding(Label l, object s, PropertyAccessor a) { _label = l; _source = s; _acc = a; }
        public void Refresh()
        {
            if (_acc != null && _acc.TryGet(_source, out object v))
                _label.Text = v?.ToString() ?? "";
        }
    }

    sealed class Unbinder<T> : IDisposable
    {
        INotifyValueChanged<T> _c; Action<T, T> _h;
        public Unbinder(INotifyValueChanged<T> c, Action<T, T> h) { _c = c; _h = h; }
        public void Dispose() { if (_c != null && _h != null) _c.ValueChanged -= _h; _c = null; _h = null; }
    }

    sealed class Noop : IDisposable { public static readonly Noop Instance = new(); public void Dispose() { } }
}

// Cached property/field accessor (reflection once, then delegate-free get/set via MemberInfo). Supports a
// dotted path one level deep ("Stats.Health") for nested objects.
sealed class PropertyAccessor
{
    readonly PropertyInfo _prop;
    readonly FieldInfo _field;
    readonly PropertyAccessor _next;   // for dotted paths

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
