namespace BallisticEngine.Editor.Inspector;

public sealed class EnableStep : IDrawerStep {
    public string Key => "BallisticEngine.Drawers.Enable";
    public bool Draw(IProperty p, IInspectorGui gui, Func<bool> next) {
        bool disabled = p.Attributes.ReadOnly || Conditions.Disabled(p.Attributes.Conditionals, p.Owner);
        if (!disabled) return next();
        gui.BeginDisabled();
        try { return next(); }
        finally { gui.EndDisabled(); }
    }
}
