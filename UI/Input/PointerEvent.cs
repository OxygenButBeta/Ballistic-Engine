using OpenTK.Mathematics;

namespace BallisticEngine.UI;

// Which mouse button a pointer event came from. Mirrors the web's MouseEvent.button ordering.
public enum PointerButton { Left, Right, Middle }

// A pointer event delivered to a VisualElement's callbacks. Carries the panel-space position and the
// button involved. `Handled` lets a callback stop the event from bubbling further up the ancestor
// chain — the equivalent of stopPropagation() in the DOM / a handled UnityEvent.
public sealed class PointerEvent
{
    public Vector2 Position { get; internal set; }
    public PointerButton Button { get; internal set; }

    // The element the event was originally dispatched to (deepest hit), even while bubbling upward.
    public VisualElement Target { get; internal set; }

    // Set by a handler to halt bubbling. The dispatcher checks this after each ancestor.
    public bool Handled { get; set; }

    internal void Reset(Vector2 pos, PointerButton button, VisualElement target)
    {
        Position = pos; Button = button; Target = target; Handled = false;
    }
}
