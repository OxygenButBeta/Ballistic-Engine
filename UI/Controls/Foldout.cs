using System;

namespace BallisticEngine.UI;

// A collapsible section (P5.6) — UITK's Foldout. A clickable header (arrow + title) that shows/hides a
// content container. Children added to the Foldout go into the content. Open state toggles the "open"
// class and the content's display.
public class Foldout : VisualElement, INotifyValueChanged<bool>
{
    readonly VisualElement _header;
    readonly Label _arrow;
    readonly Label _title;
    public VisualElement ContentContainer { get; }

    public event Action<bool, bool> ValueChanged;

    bool _open = true;
    public bool Value
    {
        get => _open;
        set { if (_open == value) return; bool old = _open; SetValueWithoutNotify(value); ValueChanged?.Invoke(old, _open); }
    }
    public void SetValueWithoutNotify(bool value)
    {
        _open = value;
        EnableInClassList("open", value);
        _arrow.Text = value ? "▼" : "▶";   // ▼ / ▶
        ContentContainer.Style.Display = value ? DisplayStyle.Flex : DisplayStyle.None;
    }

    public string Text { get => _title.Text; set => _title.Text = value; }

    public Foldout(string text = "")
    {
        AddToClassList("foldout");
        Style.FlexDirection = FlexDirection.Column;

        _header = new Panel();
        _header.AddToClassList("foldout-header");
        _header.Focusable = true;
        _header.Style.FlexDirection = FlexDirection.Row;
        _header.Style.AlignItems = Align.Center;
        _header.Style.Gap = 6;
        _arrow = new Label("▼"); _arrow.PickingEnabled = false;
        _title = new Label(text); _title.PickingEnabled = false;
        _header.Add(_arrow); _header.Add(_title);
        base.Add(_header);

        ContentContainer = new Panel();
        ContentContainer.AddToClassList("foldout-content");
        ContentContainer.Style.FlexDirection = FlexDirection.Column;
        ContentContainer.Style.SetPadding(Edge.Left, 14);
        base.Add(ContentContainer);

        _header.PointerClick += _ => Value = !Value;
        _header.KeyDown += e =>
        {
            if (e.Handled) return;
            if (e.Key is OpenTK.Windowing.GraphicsLibraryFramework.Keys.Space
                or OpenTK.Windowing.GraphicsLibraryFramework.Keys.Enter)
            { Value = !Value; e.Handled = true; }
        };
    }

    public new void Add(VisualElement child) => ContentContainer.Add(child);
    public new void Clear() => ContentContainer.Clear();
}
