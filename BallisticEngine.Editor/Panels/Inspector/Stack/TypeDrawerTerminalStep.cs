namespace BallisticEngine.Editor.Inspector;

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
