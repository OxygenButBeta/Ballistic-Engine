namespace BallisticEngine.Editor.Inspector;

public interface ITypeDrawer {
    bool CanDraw(Type valueType);
    bool Draw(IProperty property, IInspectorGui gui);
}
