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
        set { _text = value ?? ""; Layout.MarkDirtyIfMeasured(); }
    }

    public TextAlign TextAlign { get; set; } = TextAlign.MiddleLeft;

    public Label() { InstallMeasure(); }
    public Label(string text) { _text = text ?? ""; InstallMeasure(); }

    void InstallMeasure()
    {
        Layout.SetMeasure((availW, availH) =>
        {
            if (string.IsNullOrEmpty(_text)) return (0f, Style.FontSize * 1.2f);
            var font = UIFonts.Resolve(Style.FontFamily);
            if (font != null)
            {
                var (w, h) = font.Measure(_text, Style.FontSize, Style.LetterSpacing);
                return (w, h);
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
