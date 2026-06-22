namespace BallisticEngine.Editor.Inspector;

public sealed class VisibilityStep : IDrawerStep {
    public string Key => "BallisticEngine.Drawers.Conditional.Visibility";
    public bool Draw(IProperty p, IInspectorGui gui, Func<bool> next) {
        if (!Conditions.Visible(p.Attributes.Conditionals, p.Owner))
            return false;
        return next();
    }
}

public sealed class HeaderSpaceStep : IDrawerStep {
    public string Key => "BallisticEngine.Drawers.HeaderSpace";
    public bool Draw(IProperty p, IInspectorGui gui, Func<bool> next) {
        if (p.Attributes.Space is { } s) gui.Space(s.Height);
        if (p.Attributes.Header is { } h) gui.Header(h.Text);
        return next();
    }
}

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

public sealed class TypeDrawerTerminalStep : IDrawerStep {
    readonly DrawerRegistry registry;
    public TypeDrawerTerminalStep(DrawerRegistry registry) => this.registry = registry;

    public string Key => "BallisticEngine.Drawers.Terminal.TypeDrawer";
    public bool Draw(IProperty p, IInspectorGui gui, Func<bool> next) {
        ITypeDrawer drawer = registry.Resolve(p.ValueType);
        if (drawer is not null)
            return drawer.Draw(p, gui);
        gui.Unsupported(p.ValueType);
        return false;
    }
}
