namespace BallisticEngine.Editor;

public interface IEditorInput {
    Vector2 MousePos { get; }
    Vector2 MouseDelta { get; }
    float MouseWheel { get; }
    bool MouseClicked(int button);
    bool MouseDoubleClicked(int button);
    bool MouseDown(int button);
    bool MouseReleased(int button);
    bool MouseDragging(int button);
    bool KeyPressed(EditorGuiKey key);
    bool InvisibleButton(string id, Vector2 size);
}
