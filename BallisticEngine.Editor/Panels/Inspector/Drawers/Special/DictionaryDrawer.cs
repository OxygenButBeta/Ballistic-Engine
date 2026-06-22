namespace BallisticEngine.Editor.Inspector;

public sealed class DictionaryDrawer : ITypeDrawer {
    readonly IComponentInspectorHost host;
    public DictionaryDrawer(IComponentInspectorHost host) => this.host = host;
    public bool CanDraw(Type t) =>
        t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>);
    public bool Draw(IProperty p, IInspectorGui gui) {
        host.DrawDictionarySlot(p);
        return false;
    }
}
