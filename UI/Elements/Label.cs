namespace BallisticEngine.UI;

public class Label : VisualElement
{
    string _text = "";
    public string Text
    {
        get => _text;
        set { if (_text == (value ?? "")) return; _text = value ?? ""; Layout.MarkDirtyIfMeasured(); }
    }

    public TextAlign TextAlign { get; set; } = TextAlign.MiddleLeft;

    float _measuredFontSize = -1, _measuredLetterSpacing;
    string _measuredFamily;
    int _measuredFontVersion = -1;
    bool _measuredWrap;

    public Label() { InstallMeasure(); }
    public Label(string text) { _text = text ?? ""; InstallMeasure(); }

    internal void RefreshMeasureIfStale()
    {
        bool wrap = Style.WhiteSpace == WhiteSpace.Normal;
        if (Style.FontSize != _measuredFontSize || Style.FontFamily != _measuredFamily ||
            Style.LetterSpacing != _measuredLetterSpacing || UIFonts.Version != _measuredFontVersion ||
            wrap != _measuredWrap)
        {
            Layout.MarkDirtyIfMeasured();
        }
    }

    void InstallMeasure()
    {
        Layout.SetMeasure((availW, wMode, availH, hMode) =>
        {
            _measuredFontSize = Style.FontSize;
            _measuredFamily = Style.FontFamily;
            _measuredLetterSpacing = Style.LetterSpacing;
            _measuredFontVersion = UIFonts.Version;
            bool wrap = Style.WhiteSpace == WhiteSpace.Normal;
            _measuredWrap = wrap;

            if (string.IsNullOrEmpty(_text)) return (0f, Style.FontSize * 1.2f);
            var font = UIFonts.Resolve(Style.FontFamily);
            if (font != null)
            {
                if (wrap && (wMode == MeasureMode.AtMost || wMode == MeasureMode.Exactly) && availW > 0 && !float.IsNaN(availW))
                    return font.MeasureWrapped(_text, Style.FontSize, Style.LetterSpacing, availW);
                return font.Measure(_text, Style.FontSize, Style.LetterSpacing);
            }

            float em = Style.FontSize;
            return (_text.Length * em * 0.55f + _text.Length * Style.LetterSpacing, em * 1.2f);
        });
    }
}

public enum TextAlign
{
    UpperLeft, UpperCenter, UpperRight,
    MiddleLeft, MiddleCenter, MiddleRight,
    LowerLeft, LowerCenter, LowerRight,
}
