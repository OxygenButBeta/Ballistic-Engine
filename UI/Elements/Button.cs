namespace BallisticEngine.UI;

public class Button : Label
{
    public event Action Clicked;

    public Button() { Init(); }
    public Button(string text) : base(text) { Init(); }

    void Init()
    {
        TextAlign = TextAlign.MiddleCenter;
        Focusable = true; Role = "button";
        KeyDown += e =>
        {
            if (e.Handled || !Enabled) return;
            if (e.Key is OpenTK.Windowing.GraphicsLibraryFramework.Keys.Enter
                or OpenTK.Windowing.GraphicsLibraryFramework.Keys.KeyPadEnter
                or OpenTK.Windowing.GraphicsLibraryFramework.Keys.Space)
            {
                InvokeClick();
                e.Handled = true;
            }
        };
    }

    internal void InvokeClick() => Clicked?.Invoke();

    public bool Enabled
    {
        get => !ClassListContains("disabled");
        set => EnableInClassList("disabled", !value);
    }
}
