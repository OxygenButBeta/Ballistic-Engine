namespace BallisticEngine.Editor.Inspector;

// A cross-cutting modifier applied to a property before its value widget is drawn. Each reads the
// relevant attribute(s) and no-ops if absent. Used by DrawerPipeline (the volume path). Extensible: add
// a decorator to the pipeline's list and every property honours it — the seam the old per-case inline
// attribute code lacked.
public interface IPropertyDecorator {
    bool Visible(IProperty p) => true;            // false hides the whole row (ShowIf/HideIf)
    bool? Enabled(IProperty p) => null;           // false forces disabled; null = no opinion
    void BeforeRow(IProperty p, IInspectorGui gui) { }   // Header/Space/help chrome
}

// [ShowIf]/[HideIf] (visibility) + [EnableIf]/[DisableIf] (enable), delegating to the shared Conditions.
public sealed class ConditionalDecorator : IPropertyDecorator {
    public bool Visible(IProperty p) => Conditions.Visible(p.Attributes.Conditionals, p.Owner);
    public bool? Enabled(IProperty p) => Conditions.Disabled(p.Attributes.Conditionals, p.Owner) ? false : null;
}

// [ReadOnly] -> always disabled.
public sealed class ReadOnlyDecorator : IPropertyDecorator {
    public bool? Enabled(IProperty p) => p.Attributes.ReadOnly ? false : null;
}

// [Space]/[Header] chrome above the row.
public sealed class HeaderSpaceDecorator : IPropertyDecorator {
    public void BeforeRow(IProperty p, IInspectorGui gui) {
        if (p.Attributes.Space is { } s) gui.Space(s.Height);
        if (p.Attributes.Header is { } h) gui.Header(h.Text);
    }
}
