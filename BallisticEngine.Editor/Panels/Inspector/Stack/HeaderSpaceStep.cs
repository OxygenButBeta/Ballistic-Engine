namespace BallisticEngine.Editor.Inspector;

public sealed class HeaderSpaceStep : IDrawerStep {
    public string Key => "BallisticEngine.Drawers.HeaderSpace";
    public bool Draw(IProperty p, IInspectorGui gui, Func<bool> next) {
        if (p.Attributes.Space is { } s) gui.Space(s.Height);
        if (p.Attributes.Header is { } h) gui.Header(h.Text);
        return next();
    }
}
