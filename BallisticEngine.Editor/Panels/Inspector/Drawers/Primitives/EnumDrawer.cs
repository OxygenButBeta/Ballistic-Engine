using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;

namespace BallisticEngine.Editor.Inspector;

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
