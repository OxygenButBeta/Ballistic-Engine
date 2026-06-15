using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine.Editor;

internal enum EditorKey { W, A, S, D, Q, E, Shift }

// Per-frame snapshot of raw window input for the editor camera, taken straight from OpenTK
// (the engine's static Input facade is bound to the editor host, but the editor camera lives
// outside the component system so it reads the window directly).
internal sealed class EditorInput {
    GameWindow window;
    Vector2 lastMouse;

    public Vector2 MouseDelta { get; private set; }
    public bool RightMouseDown { get; private set; }
    public float ScrollY { get; private set; }

    public EditorInput(GameWindow window) {
        this.window = window;
        lastMouse = new Vector2(window.MouseState.X, window.MouseState.Y);
    }

    public void NewFrame() {
        MouseState m = window.MouseState;
        var pos = new Vector2(m.X, m.Y);
        MouseDelta = pos - lastMouse;
        lastMouse = pos;
        RightMouseDown = m[MouseButton.Right];
        ScrollY = m.ScrollDelta.Y;
    }

    public bool CtrlDown => window.KeyboardState.IsKeyDown(Keys.LeftControl) ||
                            window.KeyboardState.IsKeyDown(Keys.RightControl);

    public bool ShiftDown => window.KeyboardState.IsKeyDown(Keys.LeftShift) ||
                             window.KeyboardState.IsKeyDown(Keys.RightShift);

    // True only on the frame `key` transitions to down (raw OpenTK edge detection — independent of
    // ImGui focus/ownership, so global shortcuts like Ctrl+Z fire even when a panel has focus).
    public bool KeyPressed(Keys key) =>
        window.KeyboardState.IsKeyPressed(key);

    // True while `key` is held (raw OpenTK, ImGui-focus-independent) — for held modifiers like the
    // V vertex-snap key that arm a gizmo behaviour for as long as they're down.
    public bool KeyDown(Keys key) =>
        window.KeyboardState.IsKeyDown(key);

    public bool Key(EditorKey key) {
        KeyboardState k = window.KeyboardState;
        return key switch {
            EditorKey.W => k.IsKeyDown(Keys.W),
            EditorKey.A => k.IsKeyDown(Keys.A),
            EditorKey.S => k.IsKeyDown(Keys.S),
            EditorKey.D => k.IsKeyDown(Keys.D),
            EditorKey.Q => k.IsKeyDown(Keys.Q),
            EditorKey.E => k.IsKeyDown(Keys.E),
            EditorKey.Shift => k.IsKeyDown(Keys.LeftShift) || k.IsKeyDown(Keys.RightShift),
            _ => false,
        };
    }
}
