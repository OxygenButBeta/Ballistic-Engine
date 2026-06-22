namespace BallisticEngine.Editor.Inspector;

public sealed class PolymorphicDrawer : ITypeDrawer {
    readonly IComponentInspectorHost host;
    public PolymorphicDrawer(IComponentInspectorHost host) => this.host = host;

    public bool CanDraw(Type t) =>
        t is { IsInterface: true } or { IsAbstract: true } && !typeof(BObject).IsAssignableFrom(t);
    public bool Draw(IProperty p, IInspectorGui gui) {
        host.DrawPolymorphicSlot(p, p.ValueType);
        return false;
    }
}
