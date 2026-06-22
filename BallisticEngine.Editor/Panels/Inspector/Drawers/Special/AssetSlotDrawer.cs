namespace BallisticEngine.Editor.Inspector;

public sealed class AssetSlotDrawer : ITypeDrawer {
    readonly IComponentInspectorHost host;
    public AssetSlotDrawer(IComponentInspectorHost host) => this.host = host;
    public bool CanDraw(Type t) => typeof(BObject).IsAssignableFrom(t);
    public bool Draw(IProperty p, IInspectorGui gui) {
        host.DrawAssetSlot(p);
        return false;
    }
}
