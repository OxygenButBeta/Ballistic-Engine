using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;

namespace BallisticEngine.Editor.Inspector;

public sealed class StringDrawer : ITypeDrawer {
    public bool CanDraw(Type t) => t == typeof(string);
    public bool Draw(IProperty p, IInspectorGui gui) {
        string v = (string)p.Get() ?? "";
        if (!gui.InputText(ref v, 256)) return false;
        p.Set(v);
        return true;
    }
}
