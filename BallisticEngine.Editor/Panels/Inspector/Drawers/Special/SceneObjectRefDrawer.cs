namespace BallisticEngine.Editor.Inspector;

public sealed class SceneObjectRefDrawer : ITypeDrawer {
    readonly IComponentInspectorHost host;
    public SceneObjectRefDrawer(IComponentInspectorHost host) => this.host = host;
    public bool CanDraw(Type t) => t == typeof(EntityRef) || t == typeof(ComponentRef);
    public bool Draw(IProperty p, IInspectorGui gui) {
        host.DrawSceneObjectSlot(p);
        return false;
    }
}
