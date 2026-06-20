using System;

namespace BallisticEngine.UI;

// A clickable text element — analogue of HTML <button> / Unity's Button. It's a Label that also
// raises Clicked when a full press+release lands inside it (the input layer drives this). Ported
// controllers wire handlers exactly like Unity: `myButton.Clicked += OnBuy;` — so the JS onClick in
// a Claude design becomes one C# subscription.
public class Button : Label
{
    public event Action Clicked;

    public Button() { Init(); }
    public Button(string text) : base(text) { Init(); }

    void Init()
    {
        TextAlign = TextAlign.MiddleCenter;
        Focusable = true;                 // keyboard-navigable (P3.2)
        // Activate on Enter/Space when focused (P3.3 navigation submit).
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

    // Called by the input layer when a click completes on this button (and it's enabled). Kept
    // internal so only the UI input pipeline triggers it, never arbitrary code.
    internal void InvokeClick() => Clicked?.Invoke();

    // Disabled buttons don't raise Clicked and match the :disabled USS state (a class the input
    // layer/cascade can key on). Mirrors the port skill's enabled/disabled handling.
    public bool Enabled
    {
        get => !ClassListContains("disabled");
        set => EnableInClassList("disabled", !value);
    }
}
