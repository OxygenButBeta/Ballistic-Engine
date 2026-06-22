using System.Reflection;

namespace BallisticEngine.UI;

public static class Binding
{
    public static IDisposable Bind<T>(INotifyValueChanged<T> control, object source, string path, bool oneWay = false)
    {
        if (control == null || source == null || string.IsNullOrEmpty(path))
            return Noop.Instance;

        var accessor = PropertyAccessor.For(source.GetType(), path);
        if (accessor == null) return Noop.Instance;

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
