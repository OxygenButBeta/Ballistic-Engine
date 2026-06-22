namespace BallisticEngine.Editor.Inspector;

public sealed class NestedDrawer : ITypeDrawer {
    readonly IComponentInspectorHost host;
    public NestedDrawer(IComponentInspectorHost host) => this.host = host;
    public bool CanDraw(Type t) => PropertyCategories.Classify(t) == PropertyCategory.Nested;
    public bool Draw(IProperty p, IInspectorGui gui) {
        host.DrawNestedSlot(p, p.ValueType);
        return false;
    }
}
