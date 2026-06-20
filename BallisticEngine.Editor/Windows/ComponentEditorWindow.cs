using System.Numerics;

namespace BallisticEngine.Editor;

// A standalone EditorWindow that shows ONE component in a large, focused view. It reuses the inspector's
// reflection member-list renderer — the host InspectorPanel hands us a draw delegate at startup — so
// anything that draws in the narrow inspector column also draws here, just with room to breathe. One
// component is shown at a time.
//
// This is the editor-side of Unity's "custom EditorWindow" entry point, reduced to its most useful form
// (a big window for an awkward-to-author component) without leaking ImGui into game/engine code.
//
// Phase-6/8 EditorWindow: now an EditorWindow INSTANCE drawn through WindowShell (this file has zero raw
// ImGui — the only drawing is the inspector's member-render delegate, which owns its own widgets). A
// static facade (Configure / Show / CloseIf) targets the single shared Instance, exactly like the curve
// editor, because the inspector opens it by a global call.
internal sealed class ComponentEditorWindow : EditorWindow {
    public static readonly ComponentEditorWindow Instance = new();

    public ComponentEditorWindow() {
        DockKey = "win.componenteditor";
        Title = "Component";
        Icon = EditorIcons.Wrench;
        NoCollapse = true;
        DesiredSize = new Vector2(520, 480);
    }

    object target;            // the component instance being shown

    // Set once by InspectorPanel: draws a component's reflected members (its DrawMemberList). Taking it
    // as a delegate avoids a hard dependency / duplicated member-rendering here.
    static Action<Type, object> drawMembers;

    public static void Configure(Action<Type, object> memberRenderer) => drawMembers = memberRenderer;

    // Opens the window on a component. (Named Show, not Open, because EditorWindow.Open is the instance
    // show-state field.)
    public static void Show(object component, string windowTitle) {
        if (component is null) return;
        Instance.target = component;
        Instance.Title = string.IsNullOrEmpty(windowTitle) ? component.GetType().Name : windowTitle;
        Instance.Open = true;
    }

    // Drop the window if the component it shows was destroyed/removed.
    public static void CloseIf(Func<object, bool> goneIfTrue) {
        if (Instance.Open && (Instance.target is null || goneIfTrue(Instance.target))) Instance.Open = false;
    }

    protected override void OnGui(IEditorGui gui) {
        if (target is null) { Open = false; return; }
        drawMembers?.Invoke(target.GetType(), target);
    }
}
