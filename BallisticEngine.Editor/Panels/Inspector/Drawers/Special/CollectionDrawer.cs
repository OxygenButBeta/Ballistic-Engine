namespace BallisticEngine.Editor.Inspector;

public sealed class CollectionDrawer : ITypeDrawer {
    readonly IComponentInspectorHost host;
    public CollectionDrawer(IComponentInspectorHost host) => this.host = host;
    public bool CanDraw(Type t) {
        if (t == typeof(string)) return false;
        if (t.IsArray && t.GetArrayRank() == 1) return true;
        return t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>);
    }
    public bool Draw(IProperty p, IInspectorGui gui) {
        host.DrawCollectionSlot(p);
        return false;
    }
}
