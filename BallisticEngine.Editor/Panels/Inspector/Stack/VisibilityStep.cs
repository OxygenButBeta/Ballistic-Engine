namespace BallisticEngine.Editor.Inspector;

public sealed class VisibilityStep : IDrawerStep {
    public string Key => "BallisticEngine.Drawers.Conditional.Visibility";
    public bool Draw(IProperty p, IInspectorGui gui, Func<bool> next) {
        if (!Conditions.Visible(p.Attributes.Conditionals, p.Owner))
            return false;
        return next();
    }
}
