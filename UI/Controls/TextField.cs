using System;
using System.Text;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace BallisticEngine.UI;

// A single-line text input (P5.2) — UITK's TextField. Editable text with a caret, character insert,
// backspace/delete, arrow-key caret movement, Home/End, and a blinking caret while focused. Built on the
// P3 focus + keyboard + TextInput pipeline. Multiline + selection are layered on a single-line core.
//
// Renders via a child Label (the text) + a caret Panel positioned at the caret's pixel x. Value is the
// edited string; ValueChanged fires on every edit. Password mode masks the displayed glyphs.
public class TextField : VisualElement, INotifyValueChanged<string>, IPostLayout
{
    readonly Label _textLabel;
    readonly VisualElement _caret;
    readonly StringBuilder _sb = new();

    int _caretIndex;
    float _caretBlink;
    bool _multiline;

    public event Action<string, string> ValueChanged;

    // Placeholder shown (dimmed) when empty + not focused.
    public string Placeholder { get; set; } = "";
    public bool IsPassword { get; set; }
    public char MaskChar { get; set; } = '•';
    public int MaxLength { get; set; } = int.MaxValue;
    public bool Multiline { get => _multiline; set { _multiline = value; Style.WhiteSpace = value ? WhiteSpace.Normal : WhiteSpace.NoWrap; } }

    public string Value
    {
        get => _sb.ToString();
        set { string v = value ?? ""; if (v == Value) return; string old = Value; SetValueWithoutNotify(v); ValueChanged?.Invoke(old, Value); }
    }

    public void SetValueWithoutNotify(string value)
    {
        _sb.Clear();
        _sb.Append((value ?? "").Length > MaxLength ? value.Substring(0, MaxLength) : value ?? "");
        _caretIndex = Math.Clamp(_caretIndex, 0, _sb.Length);
        UpdateDisplay();
    }

    public TextField(string text = "")
    {
        AddToClassList("text-field");
        Focusable = true;
        Style.Overflow = Overflow.Hidden;
        Style.SetBorderWidth(Edge.All, 1);
        Style.BorderColor = Color.Rgb(120, 120, 120);
        Style.SetPadding(Edge.All, 4);
        Style.BackgroundColor = Color.Rgb(30, 30, 30);
        Style.AlignItems = Align.Center;

        _textLabel = new Label("");
        _textLabel.AddToClassList("text-field-text");
        _textLabel.PickingEnabled = false;
        Add(_textLabel);

        _caret = new Panel();
        _caret.AddToClassList("text-field-caret");
        _caret.Style.Position = PositionType.Absolute;
        _caret.Style.Width = Length.Points(1);
        _caret.Style.BackgroundColor = Color.White;
        _caret.Style.Display = DisplayStyle.None;
        _caret.PickingEnabled = false;
        Add(_caret);

        _sb.Append(text ?? "");
        _caretIndex = _sb.Length;

        FocusIn += () => { _caretBlink = 0f; UpdateDisplay(); };
        FocusOut += () => { _caret.Style.Display = DisplayStyle.None; UpdateDisplay(); };

        TextInput += OnChar;
        KeyDown += OnKey;
    }

    void OnChar(char c)
    {
        if (char.IsControl(c)) return;            // control chars handled in OnKey
        if (_sb.Length >= MaxLength) return;
        _sb.Insert(_caretIndex, c);
        _caretIndex++;
        Commit();
    }

    void OnKey(KeyEvent e)
    {
        if (e.Handled) return;
        switch (e.Key)
        {
            case Keys.Backspace:
                if (_caretIndex > 0) { _sb.Remove(_caretIndex - 1, 1); _caretIndex--; Commit(); }
                e.Handled = true; break;
            case Keys.Delete:
                if (_caretIndex < _sb.Length) { _sb.Remove(_caretIndex, 1); Commit(); }
                e.Handled = true; break;
            case Keys.Left: _caretIndex = Math.Max(0, _caretIndex - 1); _caretBlink = 0; UpdateDisplay(); e.Handled = true; break;
            case Keys.Right: _caretIndex = Math.Min(_sb.Length, _caretIndex + 1); _caretBlink = 0; UpdateDisplay(); e.Handled = true; break;
            case Keys.Home: _caretIndex = 0; _caretBlink = 0; UpdateDisplay(); e.Handled = true; break;
            case Keys.End: _caretIndex = _sb.Length; _caretBlink = 0; UpdateDisplay(); e.Handled = true; break;
            case Keys.Enter:
            case Keys.KeyPadEnter:
                if (_multiline) { _sb.Insert(_caretIndex, '\n'); _caretIndex++; Commit(); }
                e.Handled = true; break;
            case Keys.V when e.Ctrl:
                // paste hook — host can push clipboard text via TextInput; nothing to do here
                break;
        }
    }

    void Commit()
    {
        string old = _lastNotified;
        string now = Value;
        UpdateDisplay();
        if (now != old) { _lastNotified = now; ValueChanged?.Invoke(old, now); }
    }
    string _lastNotified = "";

    void UpdateDisplay()
    {
        string shown;
        if (_sb.Length == 0 && !IsFocused)
        {
            shown = Placeholder;
            _textLabel.Style.TextColor = Color.Rgba(255, 255, 255, 0.35f);
        }
        else
        {
            shown = IsPassword ? new string(MaskChar, _sb.Length) : _sb.ToString();
            _textLabel.Style.TextColor = Color.White;
        }
        _textLabel.Text = shown;
    }

    public void OnAfterLayout()
    {
        if (!IsFocused) { _caret.Style.Display = DisplayStyle.None; return; }
        // Position the caret at the pixel x of _caretIndex within the text.
        var font = UIFonts.Resolve(Style.FontFamily);
        float x = Style.FontSize * 0.05f;
        if (font != null)
        {
            string upto = IsPassword ? new string(MaskChar, _caretIndex) : _sb.ToString(0, Math.Min(_caretIndex, _sb.Length));
            var (w, _) = font.Measure(upto, Style.FontSize, Style.LetterSpacing);
            x = w;
        }
        var pad = 4f;
        _caret.Style.Left = pad + x;
        _caret.Style.Top = pad;
        _caret.Style.Height = Length.Points(Style.FontSize * 1.1f);
        _caret.Style.Display = DisplayStyle.Flex;
    }

    // Drive the caret blink (host/document can call per frame; optional polish).
    public void Tick(float dt)
    {
        if (!IsFocused) return;
        _caretBlink += dt;
        bool on = (_caretBlink % 1.0f) < 0.5f;
        _caret.Style.Opacity = on ? 1f : 0f;
    }
}
