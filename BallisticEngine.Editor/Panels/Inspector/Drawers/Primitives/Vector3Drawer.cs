using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;

namespace BallisticEngine.Editor.Inspector;

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
