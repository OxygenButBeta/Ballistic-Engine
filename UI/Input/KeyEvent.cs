
namespace BallisticEngine.UI;

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
