using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;

namespace BallisticEngine.Editor.Inspector;

public sealed class Vector2Drawer : ITypeDrawer {
    public bool CanDraw(Type t) => t == typeof(SysVec2);
    public bool Draw(IProperty p, IInspectorGui gui) {
        var v = (SysVec2)p.Get();
        if (!gui.DragFloat2(ref v, 0.05f)) return false;
        p.Set(v);
        return true;
    }
}
