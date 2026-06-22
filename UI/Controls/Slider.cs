namespace BallisticEngine.UI;

public class Slider : VisualElement, INotifyValueChanged<float>, IPostLayout
{
    readonly VisualElement _track;
    readonly VisualElement _fill;
    readonly VisualElement _handle;

    public float LowValue { get; set; } = 0f;
    public float HighValue { get; set; } = 1f;
    public float Step { get; set; } = 0f;
    public float PageStep { get; set; } = 0.1f;

    public event Action<float, float> ValueChanged;

    float _value;
    public float Value
    {
        get => _value;
        set { float v = Clamp(value); if (v == _value) return; float old = _value; SetValueWithoutNotify(v); ValueChanged?.Invoke(old, _value); }
    }

    public void SetValueWithoutNotify(float value)
    {
        _value = Clamp(value);
        ApplyHandle();
    }

    public Slider()
    {
        AddToClassList("slider");
        Focusable = true; Role = "slider";
        Style.Height = Length.Points(20);
        Style.JustifyContent = Justify.Center;

        _track = new Panel();
        _track.AddToClassList("slider-track");
        _track.Style.Height = Length.Points(4);
        _track.Style.BorderRadius = 2;
        _track.Style.BackgroundColor = Color.Rgb(70, 70, 70);
        _track.Style.Position = PositionType.Relative;
        Add(_track);

        _fill = new Panel();
        _fill.AddToClassList("slider-fill");
        _fill.Style.Position = PositionType.Absolute;
        _fill.Style.Left = 0; _fill.Style.Top = 0;
        _fill.Style.Height = Length.Points(4);
        _fill.Style.BorderRadius = 2;
        _fill.Style.BackgroundColor = Color.Rgb(80, 160, 255);
        _track.Add(_fill);

        _handle = new Panel();
        _handle.AddToClassList("slider-handle");
        _handle.Style.Position = PositionType.Absolute;
        _handle.Style.Top = -7;
        _handle.Style.Width = Length.Points(16);
        _handle.Style.Height = Length.Points(16);
        _handle.Style.BorderRadius = 8;
        _handle.Style.BackgroundColor = Color.White;
        _track.Add(_handle);

        PointerDown += e => { SetFromPointer(e.Position.X); e.Handled = true; };
        PointerMove += e => { if (_dragging) { SetFromPointer(e.Position.X); e.Handled = true; } };
        PointerDown += e => _dragging = true;
        PointerUp += e => _dragging = false;

        KeyDown += e =>
        {
            if (e.Handled) return;
            float range = HighValue - LowValue;
            float inc = Step > 0 ? Step : range * 0.02f;
            switch (e.Key)
            {
                case OpenTK.Windowing.GraphicsLibraryFramework.Keys.Left:
                case OpenTK.Windowing.GraphicsLibraryFramework.Keys.Down: Value -= inc; e.Handled = true; break;
                case OpenTK.Windowing.GraphicsLibraryFramework.Keys.Right:
                case OpenTK.Windowing.GraphicsLibraryFramework.Keys.Up: Value += inc; e.Handled = true; break;
                case OpenTK.Windowing.GraphicsLibraryFramework.Keys.Home: Value = LowValue; e.Handled = true; break;
                case OpenTK.Windowing.GraphicsLibraryFramework.Keys.End: Value = HighValue; e.Handled = true; break;
            }
        };
    }

    bool _dragging;

    void SetFromPointer(float pointerX)
    {
        var tr = _track.ResolvedRect;
        if (tr.Width <= 0) return;
        float t = Math.Clamp((pointerX - tr.X) / tr.Width, 0f, 1f);
        Value = LowValue + t * (HighValue - LowValue);
    }

    float Clamp(float v)
    {
        v = Math.Clamp(v, Math.Min(LowValue, HighValue), Math.Max(LowValue, HighValue));
        if (Step > 0f)
            v = LowValue + (float)Math.Round((v - LowValue) / Step) * Step;
        return v;
    }

    void ApplyHandle()
    {
        var tr = _track.ResolvedRect;
        float range = HighValue - LowValue;
        float t = range != 0 ? (_value - LowValue) / range : 0f;
        float w = tr.Width;
        _fill.Style.Width = Length.Points(w * t);
        _handle.Style.Left = w * t - 8;
    }

    public void OnAfterLayout() => ApplyHandle();
}
