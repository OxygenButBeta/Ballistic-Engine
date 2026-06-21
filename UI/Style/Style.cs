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

    // --- imperative-override preservation (Unity parity for element.style.*) --------------------------
    // Code/controls that set Style.* directly (a Slider sizing its track in its ctor; game code doing
    // btn.Style.Width = 100) must SURVIVE a USS resolve. The resolver records the imperative declarations
    // an element carries (captured the moment it's first resolved, as the diff from defaults) and re-applies
    // them as the HIGHEST-precedence layer — exactly like UITK's inline element.style. We capture them ONCE
    // (the first resolve, before any cascade has run) into _imperativeOverrides; subsequent resolves replay
    // them. A control's ctor runs before the first resolve, so its ctor styles are captured.
    string _imperativeOverrides;          // serialized "prop:val;" of the imperative deltas, or null
    bool _capturedOverrides;

    // Capture the current style as imperative overrides (diff vs a fresh default Style), once. Returns the
    // serialized overrides to re-apply after reset+cascade.
    internal string CaptureImperativeOverrides()
    {
        if (_capturedOverrides) return _imperativeOverrides;
        _capturedOverrides = true;
        _imperativeOverrides = StyleSerialize.DiffFromDefaults(this);
        return _imperativeOverrides;
    }

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

    // CSS direction (P9.1) — RTL mirrors the flex main axis. Set on the root (Yoga propagates). Inherited.
    LayoutDirection _direction = LayoutDirection.LTR;
    public LayoutDirection Direction { get => _direction; set { _direction = value; L.Direction = value; } }

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
    //
    // Yoga has no readback for edge values, so Style mirrors every edge it writes into per-edge caches
    // (NaN = unset, the CSS default). This lets the editor inspector + the serializer READ what was set
    // (the visual UI Builder needs per-edge readback for insets/margin/padding/border). The cache is the
    // single source of truth for "what was authored"; Yoga remains the layout authority.
    readonly float[] _inset = { float.NaN, float.NaN, float.NaN, float.NaN };   // L,T,R,B (points)
    readonly float[] _margin = { float.NaN, float.NaN, float.NaN, float.NaN };
    readonly float[] _padding = { float.NaN, float.NaN, float.NaN, float.NaN };
    readonly float[] _border = { 0f, 0f, 0f, 0f };

    static int EdgeIndex(Edge e) => e switch { Edge.Left => 0, Edge.Top => 1, Edge.Right => 2, Edge.Bottom => 3, _ => -1 };

    // Position offsets when Position is Absolute/Relative — CSS top/right/bottom/left.
    public void SetInset(Edge edge, Length len)
    {
        ApplyLengthEdge(edge, len, L.SetPositionPoints, L.SetPositionPercent);
        Cache(_inset, edge, len.Unit == Length.Kind.Auto ? float.NaN : len.Value);
    }
    public float GetInset(Edge edge) { int i = EdgeIndex(edge); return i < 0 ? float.NaN : _inset[i]; }
    public float Left   { get => _inset[0]; set { L.SetPositionPoints(Edge.Left, value);   _inset[0] = value; } }
    public float Top    { get => _inset[1]; set { L.SetPositionPoints(Edge.Top, value);    _inset[1] = value; } }
    public float Right  { get => _inset[2]; set { L.SetPositionPoints(Edge.Right, value);  _inset[2] = value; } }
    public float Bottom { get => _inset[3]; set { L.SetPositionPoints(Edge.Bottom, value); _inset[3] = value; } }

    public void SetMargin(Edge edge, float points) { L.SetMarginPoints(edge, points); Cache(_margin, edge, points); }
    public void SetPadding(Edge edge, float points) { L.SetPaddingPoints(edge, points); Cache(_padding, edge, points); }
    public float GetMargin(Edge edge) { int i = EdgeIndex(edge); return i < 0 ? float.NaN : _margin[i]; }
    public float GetPadding(Edge edge) { int i = EdgeIndex(edge); return i < 0 ? float.NaN : _padding[i]; }
    public float GetBorderWidth(Edge edge) { int i = EdgeIndex(edge); return i < 0 ? 0f : _border[i]; }

    static void Cache(float[] arr, Edge edge, float v)
    {
        if (edge == Edge.All) { arr[0] = arr[1] = arr[2] = arr[3] = v; return; }
        int i = EdgeIndex(edge);
        if (i >= 0) arr[i] = v;
    }

    // Border width feeds BOTH the layout (Yoga insets content by the border) AND the renderer (it
    // draws a stroke of this width). v1 draws a uniform border, so the visual width tracks the last
    // non-zero edge written (Edge.All is the common path via the StyleApplier).
    public float BorderWidthVisual { get; private set; }
    public void SetBorderWidth(Edge edge, float points)
    {
        L.SetBorderPoints(edge, points);
        Cache(_border, edge, points);
        BorderWidthVisual = points;
    }

    // Shorthand: same value on all four edges (CSS `margin: 8px` / `padding: 12px`).
    public float Margin { set => SetMargin(Edge.All, value); }
    public float Padding { set => SetPadding(Edge.All, value); }

    // CSS gap / row-gap / column-gap — spacing between flex items (P4.5).
    float _gap, _rowGap, _columnGap;
    public float Gap { get => _gap; set { _gap = value; L.SetGap(Gutter.All, value); } }
    public float RowGap { get => _rowGap; set { _rowGap = value; L.SetGap(Gutter.Row, value); } }
    public float ColumnGap { get => _columnGap; set { _columnGap = value; L.SetGap(Gutter.Column, value); } }

    // CSS aspect-ratio (w/h). 0/NaN = unset (P4.6).
    float _aspectRatio = float.NaN;
    public float AspectRatio { get => _aspectRatio; set { _aspectRatio = value; L.AspectRatio = value; } }

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

    // Text alignment within the element's box (CSS text-align × vertical). Null = use the Label's own
    // default (MiddleLeft). Set via USS `text-align` / `-unity-text-align`; the Label reads it.
    public TextAlign? TextAlign;

    // CSS text-shadow: an offset + blur radius + color drawn behind the glyphs. Used both for
    // legibility (dark drop shadow) and glow (large blur, colored). HasTextShadow gates it.
    public bool HasTextShadow;
    public float TextShadowOffsetX, TextShadowOffsetY, TextShadowBlur;
    public Color TextShadowColor = Color.Transparent;

    // Text flow (P4.8). WhiteSpace.Normal wraps to the box width; NoWrap keeps one line. TextOverflow
    // controls what happens when a NoWrap line is clipped by overflow:hidden (Clip or Ellipsis). These
    // INHERIT like CSS text properties.
    public WhiteSpace WhiteSpace = WhiteSpace.NoWrap;   // Unity/UITK default is nowrap
    public TextOverflow TextOverflow = TextOverflow.Clip;

    // CSS box-shadow (P6.1): an offset + blur + spread + color drop shadow drawn BEHIND the element's box.
    // HasBoxShadow gates it. (Single shadow in v1; CSS allows a list.)
    public bool HasBoxShadow;
    public float BoxShadowOffsetX, BoxShadowOffsetY, BoxShadowBlur, BoxShadowSpread;
    public Color BoxShadowColor = Color.Transparent;

    // CSS backdrop-filter: blur(px) (P6.2) — frosted-glass: the scene/UI behind this element is blurred
    // within its box before the element draws. 0 = off.
    public float BackdropBlur;

    // Font weight/style (P6.4): selects a bold/italic atlas variant by family-name convention when the
    // renderer has one registered (e.g. "Inter" + Bold -> "Inter-Bold").
    public bool Bold;
    public bool Italic;

    // ---------------------------------------------------------------- helpers

    // P2.1 — reset EVERY property to its CSS/web default and push the defaults through to the LayoutNode.
    // The resolved-style pipeline (StyleResolver) calls this before re-applying inherited + matched + inline
    // declarations, so a removed class or a cleared :hover state REVERTS to base instead of sticking (the
    // additive-cascade bug). Layout props go through the setters so Yoga is reset too; visual props are
    // assigned directly. Keep this in sync with the field initializers above.
    public void ResetToDefaults()
    {
        // layout: flex container
        FlexDirection = FlexDirection.Row;
        FlexWrap = FlexWrap.NoWrap;
        JustifyContent = Justify.FlexStart;
        AlignItems = Align.Stretch;
        AlignContent = Align.FlexStart;
        AlignSelf = Align.Auto;
        // layout: flex item
        FlexGrow = 0f;
        FlexShrink = 1f;
        FlexBasis = Length.Auto;
        Position = PositionType.Relative;
        Display = DisplayStyle.Flex;
        Overflow = Overflow.Visible;
        Direction = LayoutDirection.LTR;
        // layout: box size
        Width = Length.Auto;
        Height = Length.Auto;
        MinWidth = 0f; MinHeight = 0f; MaxWidth = float.NaN; MaxHeight = float.NaN;
        Gap = 0f; RowGap = 0f; ColumnGap = 0f;
        AspectRatio = float.NaN;
        // layout: edges (reset all four on each — through SetX so the per-edge caches reset too; insets
        // reset to NaN = unset, the CSS default, so a from-scratch resolve doesn't inherit a stale inset)
        SetMargin(Edge.All, 0f);
        SetPadding(Edge.All, 0f);
        SetBorderWidth(Edge.All, 0f);
        _inset[0] = _inset[1] = _inset[2] = _inset[3] = float.NaN;
        L.SetPositionPoints(Edge.Left, 0f); L.SetPositionPoints(Edge.Top, 0f);
        L.SetPositionPoints(Edge.Right, 0f); L.SetPositionPoints(Edge.Bottom, 0f);
        // visual
        BackgroundColor = Color.Transparent;
        BackgroundGradient = null;
        BorderColor = Color.Transparent;
        BorderRadius = 0f;
        TextColor = Color.White;
        FontSize = 14f;
        Opacity = 1f;
        TranslateX = 0f; TranslateY = 0f; RotationDegrees = 0f; Scale = 1f;
        FontFamily = null;
        LetterSpacing = 0f;
        TextAlign = null;
        HasTextShadow = false;
        TextShadowOffsetX = 0f; TextShadowOffsetY = 0f; TextShadowBlur = 0f;
        TextShadowColor = Color.Transparent;
        WhiteSpace = WhiteSpace.NoWrap;
        TextOverflow = TextOverflow.Clip;
        HasBoxShadow = false;
        BoxShadowOffsetX = 0f; BoxShadowOffsetY = 0f; BoxShadowBlur = 0f; BoxShadowSpread = 0f;
        BoxShadowColor = Color.Transparent;
        BackdropBlur = 0f;
        Bold = false; Italic = false;
    }

    // Inherited properties (CSS-inherited subset, P2.3): a child that doesn't override these takes the
    // parent's RESOLVED value. Copies from `parent` into this style as the inheritance baseline, BEFORE
    // matched rules/inline run (so an explicit child rule still wins). Mirrors UITK's inherited set.
    public void InheritFrom(Style parent)
    {
        if (parent == null) return;
        TextColor = parent.TextColor;
        FontSize = parent.FontSize;
        FontFamily = parent.FontFamily;
        LetterSpacing = parent.LetterSpacing;
        TextAlign = parent.TextAlign;
        WhiteSpace = parent.WhiteSpace;
        TextOverflow = parent.TextOverflow;
        Bold = parent.Bold;
        Italic = parent.Italic;
        Direction = parent.Direction;
        // visibility-ish: opacity is NOT inherited in CSS (it composites) — the walker already multiplies
        // opacity down the tree, so we leave Opacity per-element here.
    }

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
