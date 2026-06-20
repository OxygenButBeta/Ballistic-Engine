using System;
using System.Collections.Generic;

namespace BallisticEngine.UI;

// A dropdown / popup select (P5.5) — UITK's DropdownField. A button showing the current choice; clicking
// opens a popup list (added to the document's overlay layer so it draws above everything). Selecting an
// item closes the popup and fires ValueChanged with the chosen index.
public class Dropdown : VisualElement, INotifyValueChanged<int>
{
    readonly Label _current;
    readonly List<string> _choices = new();
    VisualElement _popup;

    public event Action<int, int> ValueChanged;
    public IReadOnlyList<string> Choices => _choices;

    int _index = -1;
    public int Value
    {
        get => _index;
        set { if (value == _index) return; int old = _index; SetValueWithoutNotify(value); ValueChanged?.Invoke(old, _index); }
    }
    public void SetValueWithoutNotify(int value)
    {
        _index = value;
        _current.Text = (value >= 0 && value < _choices.Count) ? _choices[value] : "";
    }

    public string SelectedText => _index >= 0 && _index < _choices.Count ? _choices[_index] : "";

    public Dropdown(IEnumerable<string> choices = null)
    {
        AddToClassList("dropdown");
        Focusable = true;
        Style.FlexDirection = FlexDirection.Row;
        Style.AlignItems = Align.Center;
        Style.JustifyContent = Justify.SpaceBetween;
        Style.SetPadding(Edge.All, 6);
        Style.SetBorderWidth(Edge.All, 1);
        Style.BorderColor = Color.Rgb(120, 120, 120);
        Style.BorderRadius = 4;
        Style.BackgroundColor = Color.Rgb(40, 40, 40);

        _current = new Label(""); _current.PickingEnabled = false;
        Add(_current);
        var arrow = new Label("▾"); arrow.PickingEnabled = false; Add(arrow);

        if (choices != null) foreach (var c in choices) _choices.Add(c);
        if (_choices.Count > 0) SetValueWithoutNotify(0);

        PointerClick += _ => Toggle();
        KeyDown += e =>
        {
            if (e.Handled) return;
            switch (e.Key)
            {
                case OpenTK.Windowing.GraphicsLibraryFramework.Keys.Enter:
                case OpenTK.Windowing.GraphicsLibraryFramework.Keys.Space: Toggle(); e.Handled = true; break;
                case OpenTK.Windowing.GraphicsLibraryFramework.Keys.Down: Value = Math.Min(_choices.Count - 1, _index + 1); e.Handled = true; break;
                case OpenTK.Windowing.GraphicsLibraryFramework.Keys.Up: Value = Math.Max(0, _index - 1); e.Handled = true; break;
                case OpenTK.Windowing.GraphicsLibraryFramework.Keys.Escape: Close(); e.Handled = true; break;
            }
        };
        FocusOut += Close;
    }

    public void SetChoices(IEnumerable<string> choices)
    {
        _choices.Clear();
        foreach (var c in choices) _choices.Add(c);
        if (_index >= _choices.Count) SetValueWithoutNotify(_choices.Count - 1);
        else SetValueWithoutNotify(_index);
    }

    void Toggle() { if (_popup != null) Close(); else Open(); }

    void Open()
    {
        var overlay = OwnerDocument?.OverlayLayer;
        if (overlay == null || _choices.Count == 0) return;

        _popup = new Panel();
        _popup.AddToClassList("dropdown-popup");
        _popup.Style.Position = PositionType.Absolute;
        _popup.Style.Left = ResolvedRect.X;
        _popup.Style.Top = ResolvedRect.Y + ResolvedRect.Height;
        _popup.Style.Width = Length.Points(ResolvedRect.Width);
        _popup.Style.FlexDirection = FlexDirection.Column;
        _popup.Style.BackgroundColor = Color.Rgb(45, 45, 45);
        _popup.Style.SetBorderWidth(Edge.All, 1);
        _popup.Style.BorderColor = Color.Rgb(120, 120, 120);

        for (int i = 0; i < _choices.Count; i++)
        {
            int idx = i;
            var item = new Button(_choices[i]);
            item.AddToClassList("dropdown-item");
            item.Style.SetPadding(Edge.All, 6);
            item.TextAlign = TextAlign.MiddleLeft;
            item.Clicked += () => { Value = idx; Close(); };
            _popup.Add(item);
        }
        overlay.Add(_popup);
        EnableInClassList("open", true);
    }

    void Close()
    {
        if (_popup == null) return;
        _popup.RemoveFromHierarchy();
        _popup = null;
        EnableInClassList("open", false);
    }
}
