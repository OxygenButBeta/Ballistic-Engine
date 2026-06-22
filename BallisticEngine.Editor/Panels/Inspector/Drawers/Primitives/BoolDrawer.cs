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
