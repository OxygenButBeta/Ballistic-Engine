namespace BallisticEngine.Editor.Inspector;

public sealed class BEventDrawer : ITypeDrawer {
    public bool CanDraw(Type t) => typeof(BEvent).IsAssignableFrom(t);
    public bool Draw(IProperty p, IInspectorGui gui) {
        BEventEditor.Draw(p.Name, p.Get() as BEvent);
        return false;
    }
}
