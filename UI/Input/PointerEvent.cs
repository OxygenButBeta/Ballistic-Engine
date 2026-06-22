
namespace BallisticEngine.UI;

public enum PointerButton { Left, Right, Middle }

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
