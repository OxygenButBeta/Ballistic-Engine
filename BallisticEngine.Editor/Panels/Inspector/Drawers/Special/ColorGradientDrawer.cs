namespace BallisticEngine.Editor.Inspector;

public sealed class ColorGradientDrawer : ITypeDrawer {
    readonly IComponentInspectorHost host;
    public ColorGradientDrawer(IComponentInspectorHost host) => this.host = host;
    public bool CanDraw(Type t) => typeof(ColorGradient).IsAssignableFrom(t);
    public bool Draw(IProperty p, IInspectorGui gui) {
        if (p.Get() is not ColorGradient gradient) { gui.Unsupported(p.ValueType); return false; }
        bool changed = EditorWidgets.GradientEditor(p.Name, gradient);
        if (changed) host.MarkViewportDirty();
        return changed;
    }
}
