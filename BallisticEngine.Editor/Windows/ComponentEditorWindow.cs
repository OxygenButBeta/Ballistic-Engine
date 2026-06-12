using Hexa.NET.ImGui;
using SysVec2 = System.Numerics.Vector2;

namespace BallisticEngine.Editor;

// A standalone EditorWindow that shows ONE component in a large, focused view (the target of a
// [EditorWindowExecutionPoint] method). It reuses the inspector's reflection member-list renderer —
// the host InspectorPanel hands us a draw delegate at startup — so anything that draws in the narrow
// inspector column also draws here, just with room to breathe. One component is shown at a time.
//
// This is the editor-side of Unity's "custom EditorWindow" entry point, reduced to its most useful
// form (a big window for an awkward-to-author component) without leaking ImGui into game/engine code.
internal static class ComponentEditorWindow {
    static bool open;
    static object target;            // the component instance being shown
    static string title = "Component";

    // Set once by InspectorPanel: draws a component's reflected members (its DrawMemberList). Taking it
    // as a delegate avoids a hard dependency / duplicated member-rendering here.
    static Action<Type, object> drawMembers;

    public static void Configure(Action<Type, object> memberRenderer) => drawMembers = memberRenderer;

    public static void Open(object component, string windowTitle) {
        if (component is null) return;
        target = component;
        title = string.IsNullOrEmpty(windowTitle) ? component.GetType().Name : windowTitle;
        open = true;
    }

    // Drop the window if the component it shows was destroyed/removed.
    public static void CloseIf(Func<object, bool> goneIfTrue) {
        if (open && (target is null || goneIfTrue(target))) open = false;
    }

    public static void Draw(float scale) {
        if (!open) return;
        if (target is null) { open = false; return; }

        ImGui.SetNextWindowSize(new SysVec2(520 * scale, 480 * scale), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin($"{EditorIcons.Wrench}  {title}###ComponentEditorWindow", ref open,
                ImGuiWindowFlags.NoCollapse)) {
            ImGui.End();
            return;
        }

        drawMembers?.Invoke(target.GetType(), target);

        ImGui.End();
    }
}
