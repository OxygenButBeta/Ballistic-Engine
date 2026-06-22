using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;

namespace BallisticEngine.Editor.Inspector;

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
