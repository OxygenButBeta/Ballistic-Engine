namespace BallisticEngine.Editor;

internal sealed class ComponentEditorWindow : EditorWindow {
    public static readonly ComponentEditorWindow Instance = new();

    public ComponentEditorWindow() {
        DockKey = "win.componenteditor";
        Title = "Component";
        Icon = EditorIcons.Wrench;
        NoCollapse = true;
        DesiredSize = new Vector2(520, 480);
    }

    object target;

    static Action<Type, object> drawMembers;

    public static void Configure(Action<Type, object> memberRenderer) => drawMembers = memberRenderer;

    public static void Show(object component, string windowTitle) {
        if (component is null) return;
        Instance.target = component;
        Instance.Title = string.IsNullOrEmpty(windowTitle) ? component.GetType().Name : windowTitle;
        Instance.Open = true;
    }

    public static void CloseIf(Func<object, bool> goneIfTrue) {
        if (Instance.Open && (Instance.target is null || goneIfTrue(Instance.target))) Instance.Open = false;
    }

    protected override void OnGui(IEditorGui gui) {
        if (target is null) { Open = false; return; }
        drawMembers?.Invoke(target.GetType(), target);
    }
}
