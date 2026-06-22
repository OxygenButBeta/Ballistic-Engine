namespace BallisticEngine.Editor.Inspector;

public sealed class AnimationCurveDrawer : ITypeDrawer {
    readonly IComponentInspectorHost host;
    public AnimationCurveDrawer(IComponentInspectorHost host) => this.host = host;
    public bool CanDraw(Type t) => typeof(AnimationCurve).IsAssignableFrom(t);
    public bool Draw(IProperty p, IInspectorGui gui) {
        if (p.Get() is not AnimationCurve curve) { gui.Unsupported(p.ValueType); return false; }
        bool changed = EditorWidgets.CurveEditor(p.Name, curve, host.MarkViewportDirty);
        if (changed) host.MarkViewportDirty();
        return changed;
    }
}
