using System;

namespace BallisticEngine.UI;

// A checkbox/toggle (P5.3) — UITK's Toggle. A clickable box with a checkmark child + optional label.
// Click (or Space/Enter when focused) flips Value, firing ValueChanged and toggling the "checked" class
// (so USS can style the on state, and :checked selectors match).
public class Toggle : VisualElement, INotifyValueChanged<bool>
{
    readonly VisualElement _box;
    readonly VisualElement _check;
    readonly Label _label;

    public event Action<bool, bool> ValueChanged;

    bool _value;
    public bool Value
    {
        get => _value;
        set { if (_value == value) return; bool old = _value; SetValueWithoutNotify(value); ValueChanged?.Invoke(old, _value); }
    }

    public void SetValueWithoutNotify(bool value)
    {
        _value = value;
        EnableInClassList("checked", value);
        _check.Style.Display = value ? DisplayStyle.Flex : DisplayStyle.None;
    }

    public string Text { get => _label.Text; set => _label.Text = value; }

    public Toggle(string text = "")
    {
        AddToClassList("toggle");
        Focusable = true;
        Style.FlexDirection = FlexDirection.Row;
        Style.AlignItems = Align.Center;
        Style.Gap = 6;

        _box = new Panel();
        _box.AddToClassList("toggle-box");
        _box.Style.Width = Length.Points(18);
        _box.Style.Height = Length.Points(18);
        _box.Style.BorderRadius = 3;
        _box.Style.SetBorderWidth(Edge.All, 2);
        _box.Style.BorderColor = Color.Rgb(160, 160, 160);
        _box.Style.AlignItems = Align.Center;
        _box.Style.JustifyContent = Justify.Center;

        _check = new Panel();
        _check.AddToClassList("toggle-check");
        _check.Style.Width = Length.Points(10);
        _check.Style.Height = Length.Points(10);
        _check.Style.BorderRadius = 2;
        _check.Style.BackgroundColor = Color.Rgb(80, 160, 255);
        _check.Style.Display = DisplayStyle.None;
        _box.Add(_check);
        Add(_box);

        _label = new Label(text);
        _label.AddToClassList("toggle-label");
        Add(_label);

        PointerClick += _ => Value = !Value;
        KeyDown += e =>
        {
            if (e.Handled) return;
            if (e.Key is OpenTK.Windowing.GraphicsLibraryFramework.Keys.Space
                or OpenTK.Windowing.GraphicsLibraryFramework.Keys.Enter)
            { Value = !Value; e.Handled = true; }
        };
    }
}
