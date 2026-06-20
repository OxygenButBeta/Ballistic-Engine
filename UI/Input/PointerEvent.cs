
namespace BallisticEngine.UI;

// Which mouse button a pointer event came from. Mirrors the web's MouseEvent.button ordering.
public enum PointerButton { Left, Right, Middle }

// A keyboard event delivered to the focused element (and bubbling up). Carries the OpenTK key plus the
// modifier state, and supports StopPropagation like a pointer event. (P3.3)
public sealed class KeyEvent
{
    public OpenTK.Windowing.GraphicsLibraryFramework.Keys Key { get; internal set; }
    public bool Shift { get; internal set; }
    public bool Ctrl { get; internal set; }
    public bool Alt { get; internal set; }
    public VisualElement Target { get; internal set; }
    public bool Handled { get; set; }

    internal void Reset(OpenTK.Windowing.GraphicsLibraryFramework.Keys key, bool shift, bool ctrl, bool alt, VisualElement target)
    {
        Key = key; Shift = shift; Ctrl = ctrl; Alt = alt; Target = target; Handled = false;
    }
}

// A pointer event delivered to a VisualElement's callbacks. Carries the panel-space position and the
// button involved. `Handled` lets a callback stop the event from bubbling further up the ancestor
// chain — the equivalent of stopPropagation() in the DOM / a handled UnityEvent.
public sealed class PointerEvent
{
    public Vector2 Position { get; internal set; }
    public PointerButton Button { get; internal set; }

    // Pointer movement since last frame (PointerMove) and wheel delta (PointerWheel). Zero otherwise.
    public Vector2 Delta { get; internal set; }
    public Vector2 ScrollDelta { get; internal set; }

    // The element the event was originally dispatched to (deepest hit), even while bubbling upward.
    public VisualElement Target { get; internal set; }

    // Set by a handler to halt bubbling. The dispatcher checks this after each ancestor.
    public bool Handled { get; set; }

    internal void Reset(Vector2 pos, PointerButton button, VisualElement target)
    {
        Position = pos; Button = button; Target = target; Handled = false;
        Delta = Vector2.Zero; ScrollDelta = Vector2.Zero;
    }
}
