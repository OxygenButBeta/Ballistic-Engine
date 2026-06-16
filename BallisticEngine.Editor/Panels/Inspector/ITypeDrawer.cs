using System;
using System.Collections.Generic;

namespace BallisticEngine.Editor.Inspector;

// Draws the VALUE widget for one logical type (the leaf of the pipeline). One drawer replaces the
// corresponding arm of BOTH old switches (InspectorPanel.DrawMember and VolumeProfileEditor.DrawParameter).
public interface ITypeDrawer {
    bool CanDraw(Type valueType);
    bool Draw(IProperty property, IInspectorGui gui);
}

// Ordered list of type drawers; the LAST registered that CanDraw a type wins, so a project/editor can
// register a custom drawer that overrides a built-in for a given type.
public sealed class DrawerRegistry {
    readonly List<ITypeDrawer> drawers = new();

    public void Register(ITypeDrawer drawer) => drawers.Add(drawer);

    public ITypeDrawer Resolve(Type valueType) {
        for (int i = drawers.Count - 1; i >= 0; i--)
            if (drawers[i].CanDraw(valueType))
                return drawers[i];
        return null;
    }

    // The headless, ImGui-free built-ins. The real editor adds AnimationCurve/ColorGradient/BObject/
    // BEvent drawers on top (they need ImGui + existing widgets), but those just Register() more.
    public static DrawerRegistry CreatePrimitive() {
        var r = new DrawerRegistry();
        r.Register(new BoolDrawer());
        r.Register(new FloatDrawer());
        r.Register(new IntDrawer());
        r.Register(new StringDrawer());
        r.Register(new EnumDrawer());
        r.Register(new Vector2Drawer());
        r.Register(new Vector3Drawer());
        // ColorParameter / [ColorUsage] Vector3 reuse Vector3Drawer (IProperty.IsColor switches the widget).
        return r;
    }
}
