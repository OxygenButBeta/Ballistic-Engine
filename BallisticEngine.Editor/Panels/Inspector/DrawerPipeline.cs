using System.Collections.Generic;

namespace BallisticEngine.Editor.Inspector;

// The single entry point that both inspector paths call instead of their old hardcoded switch. Given a
// property and a host GUI, it runs the decorator chain (visibility -> chrome -> enable) then dispatches
// to the type drawer. Component members and volume parameters differ ONLY in their IProperty + IInspectorGui
// implementations; the orchestration here is shared.
public sealed class DrawerPipeline {
    readonly DrawerRegistry registry;
    readonly IReadOnlyList<IPropertyDecorator> decorators;

    public DrawerPipeline(DrawerRegistry registry, IReadOnlyList<IPropertyDecorator> decorators) {
        this.registry = registry;
        this.decorators = decorators;
    }

    public DrawerRegistry Registry => registry;

    // The default decorator order: visibility/enable conditions, ReadOnly, then Header/Space chrome.
    public static DrawerPipeline CreateDefault(DrawerRegistry registry = null) =>
        new(registry ?? DrawerRegistry.CreatePrimitive(), new IPropertyDecorator[] {
            new ConditionalDecorator(),
            new ReadOnlyDecorator(),
            new HeaderSpaceDecorator(),
        });

    // Returns true if the value was edited this frame (false also when hidden).
    public bool Draw(IProperty property, IInspectorGui gui) {
        foreach (IPropertyDecorator d in decorators)
            if (!d.Visible(property))
                return false;

        foreach (IPropertyDecorator d in decorators)
            d.BeforeRow(property, gui);

        bool disabled = false;
        foreach (IPropertyDecorator d in decorators)
            if (d.Enabled(property) == false) { disabled = true; break; }

        gui.PushId(property.Name);
        gui.BeginRow(property);
        if (disabled) gui.BeginDisabled();

        ITypeDrawer drawer = registry.Resolve(property.ValueType);
        bool changed;
        if (drawer is not null) {
            changed = drawer.Draw(property, gui);
        } else {
            gui.Unsupported(property.ValueType);
            changed = false;
        }

        if (disabled) gui.EndDisabled();
        gui.EndRow();
        gui.PopId();
        return changed;
    }
}
