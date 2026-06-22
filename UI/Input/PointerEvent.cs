
namespace BallisticEngine.UI;

public sealed class PointerEvent
{
    public Vector2 Position { get; internal set; }
    public PointerButton Button { get; internal set; }

    public Vector2 Delta { get; internal set; }
    public Vector2 ScrollDelta { get; internal set; }

    public VisualElement Target { get; internal set; }

    public bool Handled { get; set; }

    internal void Reset(Vector2 pos, PointerButton button, VisualElement target)
    {
        Position = pos; Button = button; Target = target; Handled = false;
        Delta = Vector2.Zero; ScrollDelta = Vector2.Zero;
    }
}
