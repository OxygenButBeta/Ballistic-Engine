namespace BallisticEngine.UI;

// The per-element computed style — Ballistic's analogue of Unity's IStyle (`element.style.*`).
// Two kinds of property live here:
//   * LAYOUT props (width, flex, margin, padding, position, ...) write straight through to the
//     element's LayoutNode (the Yoga facade) so the next solve picks them up.
//   * VISUAL props (background color, border, radius, text color, font size, opacity) are stored
//     as fields here; the (deferred) IUIRenderer reads them when it walks the tree to draw.
// CSS-style naming throughout so a ported design assigns them the way the source stylesheet did.
public sealed class Style
{
    readonly VisualElement _el;
    LayoutNode L => _el.Layout;

    internal Style(VisualElement el) => _el = el;

    // ---------------------------------------------------------------- layout: flex container

    FlexDirection _flexDirection = FlexDirection.Row; // web default (matches HTML <div>)
    public FlexDirection FlexDirection { get => _flexDirection; set { _flexDirection = value; L.FlexDirection = value; } }

    FlexWrap _flexWrap = FlexWrap.NoWrap;
    public FlexWrap FlexWrap { get => _flexWrap; set { _flexWrap = value; L.FlexWrap = value; } }

    Justify _justifyContent = Justify.FlexStart;
    public Justify JustifyContent { get => _justifyContent; set { _justifyContent = value; L.JustifyContent = value; } }

    Align _alignItems = Align.Stretch;
    public Align AlignItems { get => _alignItems; set { _alignItems = value; L.AlignItems = value; } }

    Align _alignContent = Align.FlexStart;
    public Align AlignContent { get => _alignContent; set { _alignContent = value; L.AlignContent = value; } }

    Align _alignSelf = Align.Auto;
    public Align AlignSelf { get => _alignSelf; set { _alignSelf = value; L.AlignSelf = value; } }

    // ---------------------------------------------------------------- layout: flex item

    float _flexGrow;
    public float FlexGrow { get => _flexGrow; set { _flexGrow = value; L.FlexGrow = value; } }

    float _flexShrink = 1f; // web default
    public float FlexShrink { get => _flexShrink; set { _flexShrink = value; L.FlexShrink = value; } }

    Length _flexBasis = Length.Auto;
    public Length FlexBasis { get => _flexBasis; set { _flexBasis = value; ApplyLength(value, L.SetFlexBasisPoints, L.SetFlexBasisPercent, L.SetFlexBasisAuto); } }

    PositionType _position = PositionType.Relative;
    public PositionType Position { get => _position; set { _position = value; L.PositionType = value; } }

    DisplayStyle _display = DisplayStyle.Flex;
    public DisplayStyle Display { get => _display; set { _display = value; L.Display = value; } }

    Overflow _overflow = Overflow.Visible;
    public Overflow Overflow { get => _overflow; set { _overflow = value; L.Overflow = value; } }

    // ---------------------------------------------------------------- layout: box size

    Length _width = Length.Auto;
    public Length Width { get => _width; set { _width = value; ApplyLength(value, L.SetWidthPoints, L.SetWidthPercent, L.SetWidthAuto); } }

    Length _height = Length.Auto;
    public Length Height { get => _height; set { _height = value; ApplyLength(value, L.SetHeightPoints, L.SetHeightPercent, L.SetHeightAuto); } }

    float _minWidth, _minHeight, _maxWidth = float.NaN, _maxHeight = float.NaN;
    public float MinWidth { get => _minWidth; set { _minWidth = value; L.SetMinWidthPoints(value); } }
    public float MinHeight { get => _minHeight; set { _minHeight = value; L.SetMinHeightPoints(value); } }
    public float MaxWidth { get => _maxWidth; set { _maxWidth = value; L.SetMaxWidthPoints(value); } }
    public float MaxHeight { get => _maxHeight; set { _maxHeight = value; L.SetMaxHeightPoints(value); } }

    // ---------------------------------------------------------------- layout: inset / margin / padding

    // Position offsets when Position is Absolute/Relative — CSS top/right/bottom/left.
    public void SetInset(Edge edge, Length len) => ApplyLengthEdge(edge, len, L.SetPositionPoints, L.SetPositionPercent);
    public float Left { set => L.SetPositionPoints(Edge.Left, value); }
    public float Top { set => L.SetPositionPoints(Edge.Top, value); }
    public float Right { set => L.SetPositionPoints(Edge.Right, value); }
    public float Bottom { set => L.SetPositionPoints(Edge.Bottom, value); }

    public void SetMargin(Edge edge, float points) => L.SetMarginPoints(edge, points);
    public void SetPadding(Edge edge, float points) => L.SetPaddingPoints(edge, points);

    // Border width feeds BOTH the layout (Yoga insets content by the border) AND the renderer (it
    // draws a stroke of this width). Yoga has no readback for it, so we also keep a visual copy.
    // v1 draws a uniform border, so the visual width tracks the last non-zero edge written (Edge.All
    // is the common path via the StyleApplier).
    public float BorderWidthVisual { get; private set; }
    public void SetBorderWidth(Edge edge, float points)
    {
        L.SetBorderPoints(edge, points);
        BorderWidthVisual = points;
    }

    // Shorthand: same value on all four edges (CSS `margin: 8px` / `padding: 12px`).
    public float Margin { set => L.SetMarginPoints(Edge.All, value); }
    public float Padding { set => L.SetPaddingPoints(Edge.All, value); }

    // ---------------------------------------------------------------- visual (renderer reads these)

    public Color BackgroundColor = Color.Transparent;

    // When set, the background is painted as this gradient INSTEAD of BackgroundColor (CSS `background:
    // linear-gradient(...)`). The renderer evaluates it inside the (rounded) box. Null = use the solid
    // BackgroundColor.
    public Gradient BackgroundGradient;

    public Color BorderColor = Color.Transparent;
    // Per-corner radius in pixels (top-left, top-right, bottom-right, bottom-left). The renderer
    // clamps each to half the box's min(width,height) so `border-radius: 999px` produces a pill, not
    // an over-arced oval — eliminating the port skill's "pills render as ovals" gotcha.
    public float BorderRadiusTopLeft, BorderRadiusTopRight, BorderRadiusBottomRight, BorderRadiusBottomLeft;
    public float BorderRadius
    {
        set => BorderRadiusTopLeft = BorderRadiusTopRight = BorderRadiusBottomRight = BorderRadiusBottomLeft = value;
    }

    public Color TextColor = Color.White;
    public float FontSize = 14f;
    public float Opacity = 1f;

    // A post-layout pixel translation applied at render time (CSS transform: translate(x,y)). Does NOT
    // affect layout — it shifts the element and its subtree visually only, like CSS transforms. Used
    // for selection slides and entrance motion. Rotation is degrees (for the small rotated gems).
    public float TranslateX, TranslateY;
    public float RotationDegrees;
    public float Scale = 1f;

    // CSS font-family — the registered UI font name to render text with (null/empty = default font).
    // Inherits down the tree at apply time isn't modeled; set it where text lives (or on a parent the
    // USS targets). Letter spacing is in pixels (CSS letter-spacing converted), added between glyphs.
    public string FontFamily;
    public float LetterSpacing;

    // CSS text-shadow: an offset + blur radius + color drawn behind the glyphs. Used both for
    // legibility (dark drop shadow) and glow (large blur, colored). HasTextShadow gates it.
    public bool HasTextShadow;
    public float TextShadowOffsetX, TextShadowOffsetY, TextShadowBlur;
    public Color TextShadowColor = Color.Transparent;

    // ---------------------------------------------------------------- helpers

    static void ApplyLength(Length len, System.Action<float> points, System.Action<float> percent, System.Action auto)
    {
        switch (len.Unit)
        {
            case Length.Kind.Points: points(len.Value); break;
            case Length.Kind.Percent: percent(len.Value); break;
            default: auto(); break;
        }
    }

    static void ApplyLengthEdge(Edge edge, Length len, System.Action<Edge, float> points, System.Action<Edge, float> percent)
    {
        if (len.Unit == Length.Kind.Percent) percent(edge, len.Value);
        else points(edge, len.Value); // auto inset is rare; treat as 0 points
    }
}
