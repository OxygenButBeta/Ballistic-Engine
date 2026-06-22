namespace BallisticEngine.Editor.Inspector;

public sealed class DrawerRegistry {
    readonly List<ITypeDrawer> drawers = new();

    public void Register(ITypeDrawer drawer) => drawers.Add(drawer);

    public ITypeDrawer Resolve(Type valueType) {
        for (int i = drawers.Count - 1; i >= 0; i--)
            if (drawers[i].CanDraw(valueType))
                return drawers[i];
        return null;
    }

    public static DrawerRegistry CreatePrimitive() {
        var r = new DrawerRegistry();
        r.Register(new BoolDrawer());
        r.Register(new FloatDrawer());
        r.Register(new IntDrawer());
        r.Register(new StringDrawer());
        r.Register(new EnumDrawer());
        r.Register(new Vector2Drawer());
        r.Register(new Vector3Drawer());
        return r;
    }
}
