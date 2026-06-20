namespace BallisticEngine.UI;

// A text element — analogue of an HTML text node / Unity's Label. Holds a string the renderer draws
// using the element's TextColor/FontSize/FontFamily style. Text alignment within the box positions
// the glyphs.
//
// The text drives intrinsic size: Label installs a measure function on its LayoutNode that asks the
// resolved font how wide/tall the string is, so flex layout sizes rows around their text (no need to
// hardcode widths). Re-measures when the text changes. Falls back to a rough estimate when no font is
// registered yet (headless before fonts load) so structure still lays out.
public class Label : VisualElement
{
    string _text = "";
    public string Text
    {
        get => _text;
        set { if (_text == (value ?? "")) return; _text = value ?? ""; Layout.MarkDirtyIfMeasured(); }
    }

    public TextAlign TextAlign { get; set; } = TextAlign.MiddleLeft;

    // Cache of the style inputs the last measure depended on, so a font-size/family/letter-spacing change
    // (P4.1) OR a font load after layout (P4.2, via UIFonts.Version) re-measures — not just a text change.
    float _measuredFontSize = -1, _measuredLetterSpacing;
    string _measuredFamily;
    int _measuredFontVersion = -1;
    bool _measuredWrap;

    public Label() { InstallMeasure(); }
    public Label(string text) { _text = text ?? ""; InstallMeasure(); }

    // Called once per layout solve (by the document) to re-dirty this label if any measure input changed.
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
            // Record what this measure depended on (for staleness detection next frame).
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
                // Wrap to the available width when white-space allows it AND we're width-constrained.
                if (wrap && (wMode == MeasureMode.AtMost || wMode == MeasureMode.Exactly) && availW > 0 && !float.IsNaN(availW))
                    return font.MeasureWrapped(_text, Style.FontSize, Style.LetterSpacing, availW);
                return font.Measure(_text, Style.FontSize, Style.LetterSpacing);
            }
            // No font yet: estimate ~0.55em per glyph so rows still get a sensible height/width.
            float em = Style.FontSize;
            return (_text.Length * em * 0.55f + _text.Length * Style.LetterSpacing, em * 1.2f);
        });
    }
}

// Mirrors CSS text-align × vertical-align combinations the port skill uses
// (-unity-text-align: middle-center, etc.).
public enum TextAlign
{
    UpperLeft, UpperCenter, UpperRight,
    MiddleLeft, MiddleCenter, MiddleRight,
    LowerLeft, LowerCenter, LowerRight,
}
