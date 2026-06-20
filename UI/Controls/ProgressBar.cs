using System;

namespace BallisticEngine.UI;

// A determinate progress bar (P5.8) — UITK's ProgressBar. A track with a fill whose width tracks
// Value in [0..1], plus an optional centered title.
public class ProgressBar : VisualElement, INotifyValueChanged<float>, IPostLayout
{
    readonly VisualElement _fill;
    readonly Label _title;

    public event Action<float, float> ValueChanged;

    float _value;
    public float Value
    {
        get => _value;
        set { float v = Math.Clamp(value, 0f, 1f); if (v == _value) return; float old = _value; SetValueWithoutNotify(v); ValueChanged?.Invoke(old, _value); }
    }
    public void SetValueWithoutNotify(float value) { _value = Math.Clamp(value, 0f, 1f); ApplyFill(); }

    public string Title { get => _title.Text; set => _title.Text = value; }

    public ProgressBar()
    {
        AddToClassList("progress-bar");
        Style.Height = Length.Points(18);
        Style.BorderRadius = 9;
        Style.BackgroundColor = Color.Rgb(50, 50, 50);
        Style.Overflow = Overflow.Hidden;
        Style.JustifyContent = Justify.Center;
        Style.AlignItems = Align.Center;
        Style.Position = PositionType.Relative;

        _fill = new Panel();
        _fill.AddToClassList("progress-fill");
        _fill.Style.Position = PositionType.Absolute;
        _fill.Style.Left = 0; _fill.Style.Top = 0; _fill.Style.Bottom = 0;
        _fill.Style.BackgroundColor = Color.Rgb(80, 160, 255);
        _fill.PickingEnabled = false;
        Add(_fill);

        _title = new Label("");
        _title.AddToClassList("progress-title");
        _title.PickingEnabled = false;
        _title.TextAlign = TextAlign.MiddleCenter;
        Add(_title);
    }

    void ApplyFill() => _fill.Style.Width = Length.Percent(_value * 100f);

    public void OnAfterLayout() => ApplyFill();
}
