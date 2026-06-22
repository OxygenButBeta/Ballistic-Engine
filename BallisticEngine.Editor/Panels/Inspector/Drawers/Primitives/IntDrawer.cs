using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;

namespace BallisticEngine.Editor.Inspector;

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
