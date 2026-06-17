using System;

namespace BallisticEngine.Editor.Inspector;

// The concrete runtime steps of the Odin-style stack (editor-rework B0). Each is the composable equivalent of
// one old flat decorator / one DrawMember branch, but now WRAPS the next step (CallNext) instead of running as
// an isolated hook. Behaviour is preserved EXACTLY (so existing components/volumes render byte-identically):
// the visibility/enable/chrome semantics are the same Conditions / MemberAttributes reads the flat path used —
// only the COMPOSITION changes from a fixed list to a deterministic resolved stack.

// Visibility (outermost): [ShowIf]/[HideIf]. Hidden → return early, the rest of the stack never draws (the
// row + its nested subtree cost nothing). Mirrors Conditions.Visible exactly (fail-open on a missing sibling).
public sealed class VisibilityStep : IDrawerStep {
    public string Key => "BallisticEngine.Drawers.Conditional.Visibility";
    public bool Draw(IProperty p, IInspectorGui gui, Func<bool> next) {
        if (!Conditions.Visible(p.Attributes.Conditionals, p.Owner))
            return false;             // hidden: do NOT call next (whole subtree skipped)
        return next();
    }
}

// Chrome: [Space] gap + [Header] separator above the row, then draw the rest. Same order as the old
// HeaderSpaceDecorator (space first, then header).
public sealed class HeaderSpaceStep : IDrawerStep {
    public string Key => "BallisticEngine.Drawers.HeaderSpace";
    public bool Draw(IProperty p, IInspectorGui gui, Func<bool> next) {
        if (p.Attributes.Space is { } s) gui.Space(s.Height);
        if (p.Attributes.Header is { } h) gui.Header(h.Text);
        return next();
    }
}

// Enable: [ReadOnly] OR an [EnableIf]/[DisableIf] that resolves to disabled → wrap the inner stack in
// BeginDisabled/EndDisabled. Matches DrawMember's `attrs.ReadOnly || Conditions.Disabled(...)` and the old
// ReadOnlyDecorator+ConditionalDecorator(Enabled) combined — but now as ONE wrap, so order can't matter.
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

// Terminal (the leaf): resolve the value drawer for the property's logical type and draw it; no inner step.
// This is the SAME DrawerRegistry resolution the flat pipeline ended in — kept exactly, so the type drawers
// (BoolDrawer/FloatDrawer/...) are unchanged terminal leaves. No drawer → Unsupported, as before.
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
