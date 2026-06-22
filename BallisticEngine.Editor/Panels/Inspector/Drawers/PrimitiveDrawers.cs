using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;

namespace BallisticEngine.Editor.Inspector;

public sealed class BoolDrawer : ITypeDrawer {
    public bool CanDraw(Type t) => t == typeof(bool);
    public bool Draw(IProperty p, IInspectorGui gui) {
        bool v = (bool)p.Get();
        if (!gui.Checkbox(ref v)) return false;
        p.Set(v);
        return true;
    }
}

public sealed class FloatDrawer : ITypeDrawer {
    public bool CanDraw(Type t) => t == typeof(float);
    public bool Draw(IProperty p, IInspectorGui gui) {
        float v = (float)p.Get();
        bool changed = p.Range is { } r ? gui.SliderFloat(ref v, r.min, r.max) : gui.DragFloat(ref v, 0.05f);
        if (!changed) return false;
        if (p.Range is { } rc) v = Math.Clamp(v, rc.min, rc.max);
        p.Set(v);
        return true;
    }
}

public sealed class IntDrawer : ITypeDrawer {
    public bool CanDraw(Type t) => t == typeof(int);
    public bool Draw(IProperty p, IInspectorGui gui) {
        int v = (int)p.Get();
        bool changed = p.Range is { } r ? gui.SliderInt(ref v, (int)r.min, (int)r.max) : gui.DragInt(ref v);
        if (!changed) return false;
        if (p.Range is { } rc) v = Math.Clamp(v, (int)rc.min, (int)rc.max);
        p.Set(v);
        return true;
    }
}

public sealed class StringDrawer : ITypeDrawer {
    public bool CanDraw(Type t) => t == typeof(string);
    public bool Draw(IProperty p, IInspectorGui gui) {
        string v = (string)p.Get() ?? "";
        if (!gui.InputText(ref v, 256)) return false;
        p.Set(v);
        return true;
    }
}

public sealed class EnumDrawer : ITypeDrawer {
    public bool CanDraw(Type t) => t.IsEnum;
    public bool Draw(IProperty p, IInspectorGui gui) {
        string[] names = Enum.GetNames(p.ValueType);
        int current = Math.Max(0, Array.IndexOf(names, p.Get().ToString()));
        if (!gui.Combo(ref current, names)) return false;
        p.Set(Enum.Parse(p.ValueType, names[current]));
        return true;
    }
}

public sealed class Vector2Drawer : ITypeDrawer {
    public bool CanDraw(Type t) => t == typeof(SysVec2);
    public bool Draw(IProperty p, IInspectorGui gui) {
        var v = (SysVec2)p.Get();
        if (!gui.DragFloat2(ref v, 0.05f)) return false;
        p.Set(v);
        return true;
    }
}

public sealed class Vector3Drawer : ITypeDrawer {
    public bool CanDraw(Type t) => t == typeof(SysVec3);
    public bool Draw(IProperty p, IInspectorGui gui) {
        var v = (SysVec3)p.Get();
        bool changed = p.IsColor ? gui.ColorEdit3(ref v, p.Hdr) : gui.DragFloat3(ref v, 0.05f);
        if (!changed) return false;
        p.Set(v);
        return true;
    }
}
