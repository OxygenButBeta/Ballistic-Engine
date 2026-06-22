using Hexa.NET.ImGui;

namespace BallisticEngine.Editor;

internal sealed class ImGuiInputAdapter : IEditorInput {
    public Vector2 MousePos => ImGui.GetMousePos();
    public Vector2 MouseDelta => ImGui.GetIO().MouseDelta;
    public float MouseWheel => ImGui.GetIO().MouseWheel;
    public bool MouseClicked(int button) => ImGui.IsMouseClicked((ImGuiMouseButton)button);
    public bool MouseDoubleClicked(int button) => ImGui.IsMouseDoubleClicked((ImGuiMouseButton)button);
    public bool MouseDown(int button) => ImGui.IsMouseDown((ImGuiMouseButton)button);
    public bool MouseReleased(int button) => ImGui.IsMouseReleased((ImGuiMouseButton)button);
    public bool MouseDragging(int button) => ImGui.IsMouseDragging((ImGuiMouseButton)button);
    public bool KeyPressed(EditorGuiKey key) => ImGui.IsKeyPressed(MapKey(key));
    public bool InvisibleButton(string id, Vector2 size) => ImGui.InvisibleButton(id, size);

    static ImGuiKey MapKey(EditorGuiKey key) => key switch {
        EditorGuiKey.F => ImGuiKey.F,
        EditorGuiKey.Delete => ImGuiKey.Delete,
        EditorGuiKey.Escape => ImGuiKey.Escape,
        EditorGuiKey.Enter => ImGuiKey.Enter,
        EditorGuiKey.LeftArrow => ImGuiKey.LeftArrow,
        EditorGuiKey.RightArrow => ImGuiKey.RightArrow,
        EditorGuiKey.UpArrow => ImGuiKey.UpArrow,
        EditorGuiKey.DownArrow => ImGuiKey.DownArrow,
        EditorGuiKey.A => ImGuiKey.A,
        EditorGuiKey.D => ImGuiKey.D,
        EditorGuiKey.G => ImGuiKey.G,
        EditorGuiKey.F2 => ImGuiKey.F2,
        EditorGuiKey.C => ImGuiKey.C,
        EditorGuiKey.X => ImGuiKey.X,
        EditorGuiKey.V => ImGuiKey.V,
        _ => ImGuiKey.None,
    };
}
