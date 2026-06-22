namespace BallisticEngine.UI;

public sealed class Style
{
    readonly VisualElement _el;
    LayoutNode L => _el.Layout;

    internal Style(VisualElement el) => _el = el;

    string _imperativeOverrides;
    bool _capturedOverrides;

    internal string CaptureImperativeOverrides()
    {
        if (_capturedOverrides) return _imperativeOverrides;
        _capturedOverrides = true;
        _imperativeOverrides = StyleSerialize.DiffFromDefaults(this);
        return _imperativeOverrides;
    }

    FlexDirection _flexDirection = FlexDirection.Row;
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

    float _flexGrow;
    public float FlexGrow { get => _flexGrow; set { _flexGrow = value; L.FlexGrow = value; } }

    float _flexShrink = 1f;
    public float FlexShrink { get => _flexShrink; set { _flexShrink = value; L.FlexShrink = value; } }

    Length _flexBasis = Length.Auto;
    public Length FlexBasis { get => _flexBasis; set { _flexBasis = value; ApplyLength(value, L.SetFlexBasisPoints, L.SetFlexBasisPercent, L.SetFlexBasisAuto); } }

    PositionType _position = PositionType.Relative;
    public PositionType Position { get => _position; set { _position = value; L.PositionType = value; } }

    DisplayStyle _display = DisplayStyle.Flex;
    public DisplayStyle Display { get => _display; set { _display = value; L.Display = value; } }

    Overflow _overflow = Overflow.Visible;
    public Overflow Overflow { get => _overflow; set { _overflow = value; L.Overflow = value; } }

    LayoutDirection _direction = LayoutDirection.LTR;
    public LayoutDirection Direction { get => _direction; set { _direction = value; L.Direction = value; } }

    Length _width = Length.Auto;
    public Length Width { get => _width; set { _width = value; ApplyLength(value, L.SetWidthPoints, L.SetWidthPercent, L.SetWidthAuto); } }

    Length _height = Length.Auto;
    public Length Height { get => _height; set { _height = value; ApplyLength(value, L.SetHeightPoints, L.SetHeightPercent, L.SetHeightAuto); } }

    float _minWidth, _minHeight, _maxWidth = float.NaN, _maxHeight = float.NaN;
    public float MinWidth { get => _minWidth; set { _minWidth = value; L.SetMinWidthPoints(value); } }
    public float MinHeight { get => _minHeight; set { _minHeight = value; L.SetMinHeightPoints(value); } }
    public float MaxWidth { get => _maxWidth; set { _maxWidth = value; L.SetMaxWidthPoints(value); } }
    public float MaxHeight { get => _maxHeight; set { _maxHeight = value; L.SetMaxHeightPoints(value); } }

    readonly float[] _inset = { float.NaN, float.NaN, float.NaN, float.NaN };
    readonly float[] _margin = { float.NaN, float.NaN, float.NaN, float.NaN };
    readonly float[] _padding = { float.NaN, float.NaN, float.NaN, float.NaN };
    readonly float[] _border = { 0f, 0f, 0f, 0f };

    static int EdgeIndex(Edge e) => e switch { Edge.Left => 0, Edge.Top => 1, Edge.Right => 2, Edge.Bottom => 3, _ => -1 };

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

    public float BorderWidthVisual { get; private set; }
    public void SetBorderWidth(Edge edge, float points)
    {
        L.SetBorderPoints(edge, points);
        Cache(_border, edge, points);
        BorderWidthVisual = points;
    }

    public float Margin { set => SetMargin(Edge.All, value); }
    public float Padding { set => SetPadding(Edge.All, value); }

    float _gap, _rowGap, _columnGap;
    public float Gap { get => _gap; set { _gap = value; L.SetGap(Gutter.All, value); } }
    public float RowGap { get => _rowGap; set { _rowGap = value; L.SetGap(Gutter.Row, value); } }
    public float ColumnGap { get => _columnGap; set { _columnGap = value; L.SetGap(Gutter.Column, value); } }

    float _aspectRatio = float.NaN;
    public float AspectRatio { get => _aspectRatio; set { _aspectRatio = value; L.AspectRatio = value; } }

    public Color BackgroundColor = Color.Transparent;

    public Gradient BackgroundGradient;

    public Color BorderColor = Color.Transparent;

    public float BorderRadiusTopLeft, BorderRadiusTopRight, BorderRadiusBottomRight, BorderRadiusBottomLeft;
    public float BorderRadius
    {
        set => BorderRadiusTopLeft = BorderRadiusTopRight = BorderRadiusBottomRight = BorderRadiusBottomLeft = value;
    }

    public Color TextColor = Color.White;
    public float FontSize = 14f;
    public float Opacity = 1f;

    public float TranslateX, TranslateY;
    public float RotationDegrees;
    public float Scale = 1f;

    public string FontFamily;
    public float LetterSpacing;

    public TextAlign? TextAlign;

    public bool HasTextShadow;
    public float TextShadowOffsetX, TextShadowOffsetY, TextShadowBlur;
    public Color TextShadowColor = Color.Transparent;

    public WhiteSpace WhiteSpace = WhiteSpace.NoWrap;
    public TextOverflow TextOverflow = TextOverflow.Clip;

    public bool HasBoxShadow;
    public float BoxShadowOffsetX, BoxShadowOffsetY, BoxShadowBlur, BoxShadowSpread;
    public Color BoxShadowColor = Color.Transparent;

    public float BackdropBlur;

    public bool Bold;
    public bool Italic;

    public void ResetToDefaults()
    {
        FlexDirection = FlexDirection.Row;
        FlexWrap = FlexWrap.NoWrap;
        JustifyContent = Justify.FlexStart;
        AlignItems = Align.Stretch;
        AlignContent = Align.FlexStart;
        AlignSelf = Align.Auto;
        FlexGrow = 0f;
        FlexShrink = 1f;
        FlexBasis = Length.Auto;
        Position = PositionType.Relative;
        Display = DisplayStyle.Flex;
        Overflow = Overflow.Visible;
        Direction = LayoutDirection.LTR;
        Width = Length.Auto;
        Height = Length.Auto;
        MinWidth = 0f; MinHeight = 0f; MaxWidth = float.NaN; MaxHeight = float.NaN;
        Gap = 0f; RowGap = 0f; ColumnGap = 0f;
        AspectRatio = float.NaN;
        SetMargin(Edge.All, 0f);
        SetPadding(Edge.All, 0f);
        SetBorderWidth(Edge.All, 0f);
        _inset[0] = _inset[1] = _inset[2] = _inset[3] = float.NaN;
        L.SetPositionPoints(Edge.Left, 0f); L.SetPositionPoints(Edge.Top, 0f);
        L.SetPositionPoints(Edge.Right, 0f); L.SetPositionPoints(Edge.Bottom, 0f);
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
        else points(edge, len.Value);
    }
}
