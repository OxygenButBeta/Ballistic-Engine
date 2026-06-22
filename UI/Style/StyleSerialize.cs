using System.Globalization;
using System.Text;

namespace BallisticEngine.UI;

public static class StyleSerialize
{
    static readonly Style Defaults = MakeDefaults();
    static Style MakeDefaults() { var p = new Panel(); return p.Style; }

    public static string DiffFromDefaults(Style s) => DiffFromBaseline(s, Defaults);

    public static string DiffFromBaseline(Style s, Style d)
    {
        var sb = new StringBuilder();

        if (!s.Width.Equals(d.Width)) Len(sb, "width", s.Width);
        if (!s.Height.Equals(d.Height)) Len(sb, "height", s.Height);
        if (s.MinWidth != d.MinWidth) Px(sb, "min-width", s.MinWidth);
        if (s.MinHeight != d.MinHeight) Px(sb, "min-height", s.MinHeight);
        if (!FloatEq(s.MaxWidth, d.MaxWidth)) Px(sb, "max-width", s.MaxWidth);
        if (!FloatEq(s.MaxHeight, d.MaxHeight)) Px(sb, "max-height", s.MaxHeight);

        if (s.FlexDirection != d.FlexDirection) Kv(sb, "flex-direction", Css(s.FlexDirection));
        if (s.FlexWrap != d.FlexWrap) Kv(sb, "flex-wrap", Css(s.FlexWrap));
        if (s.JustifyContent != d.JustifyContent) Kv(sb, "justify-content", Css(s.JustifyContent));
        if (s.AlignItems != d.AlignItems) Kv(sb, "align-items", Css(s.AlignItems));
        if (s.AlignContent != d.AlignContent) Kv(sb, "align-content", Css(s.AlignContent));
        if (s.AlignSelf != d.AlignSelf) Kv(sb, "align-self", Css(s.AlignSelf));
        if (s.FlexGrow != d.FlexGrow) Px(sb, "flex-grow", s.FlexGrow);
        if (s.FlexShrink != d.FlexShrink) Px(sb, "flex-shrink", s.FlexShrink);
        if (s.Position != d.Position) Kv(sb, "position", Css(s.Position));
        if (s.Display != d.Display) Kv(sb, "display", s.Display == DisplayStyle.None ? "none" : "flex");
        if (s.Overflow != d.Overflow) Kv(sb, "overflow", Css(s.Overflow));
        if (s.Gap != d.Gap) Px(sb, "gap", s.Gap);
        if (!FloatEq(s.AspectRatio, d.AspectRatio)) Px(sb, "aspect-ratio", s.AspectRatio);

        if (!s.BackgroundColor.Equals(d.BackgroundColor)) Col(sb, "background-color", s.BackgroundColor);
        if (!s.TextColor.Equals(d.TextColor)) Col(sb, "color", s.TextColor);
        if (!s.BorderColor.Equals(d.BorderColor)) Col(sb, "border-color", s.BorderColor);
        if (s.FontSize != d.FontSize) Px(sb, "font-size", s.FontSize);
        if (s.Opacity != d.Opacity) Px(sb, "opacity", s.Opacity);
        if (s.BorderRadiusTopLeft != d.BorderRadiusTopLeft) Px(sb, "border-radius", s.BorderRadiusTopLeft);
        if (s.BorderWidthVisual != d.BorderWidthVisual) Px(sb, "border-width", s.BorderWidthVisual);
        if (s.WhiteSpace != d.WhiteSpace) Kv(sb, "white-space", s.WhiteSpace == WhiteSpace.Normal ? "normal" : "nowrap");

        if (s.FontFamily != d.FontFamily && !string.IsNullOrEmpty(s.FontFamily)) Kv(sb, "font-family", s.FontFamily);
        if (s.LetterSpacing != d.LetterSpacing) Px(sb, "letter-spacing", s.LetterSpacing);
        if (s.Bold != d.Bold) Kv(sb, "font-weight", s.Bold ? "bold" : "normal");
        if (s.Italic != d.Italic) Kv(sb, "font-style", s.Italic ? "italic" : "normal");
        if ((s.TextAlign ?? default) != (d.TextAlign ?? default) && s.TextAlign.HasValue)
            Kv(sb, "text-align", CssAlign(s.TextAlign.Value));

        if (s.FlexBasis.Unit != d.FlexBasis.Unit || s.FlexBasis.Value != d.FlexBasis.Value) Len(sb, "flex-basis", s.FlexBasis);
        if (s.Direction != d.Direction) Kv(sb, "direction", s.Direction == LayoutDirection.RTL ? "rtl" : "ltr");
        if (s.RowGap != d.RowGap) Px(sb, "row-gap", s.RowGap);
        if (s.ColumnGap != d.ColumnGap) Px(sb, "column-gap", s.ColumnGap);

        EmitInset(sb, "left", s.GetInset(Edge.Left), d.GetInset(Edge.Left));
        EmitInset(sb, "top", s.GetInset(Edge.Top), d.GetInset(Edge.Top));
        EmitInset(sb, "right", s.GetInset(Edge.Right), d.GetInset(Edge.Right));
        EmitInset(sb, "bottom", s.GetInset(Edge.Bottom), d.GetInset(Edge.Bottom));

        EmitEdge(sb, "margin", s, d, (st, e) => st.GetMargin(e));
        EmitEdge(sb, "padding", s, d, (st, e) => st.GetPadding(e));

        if (s.BorderRadiusTopRight != s.BorderRadiusTopLeft || s.BorderRadiusBottomRight != s.BorderRadiusTopLeft ||
            s.BorderRadiusBottomLeft != s.BorderRadiusTopLeft)
        {
            Kv(sb, "border-radius", $"{F(s.BorderRadiusTopLeft)} {F(s.BorderRadiusTopRight)} {F(s.BorderRadiusBottomRight)} {F(s.BorderRadiusBottomLeft)}");
        }

        if (s.TranslateX != d.TranslateX) Px(sb, "translate-x", s.TranslateX);
        if (s.TranslateY != d.TranslateY) Px(sb, "translate-y", s.TranslateY);
        if (s.RotationDegrees != d.RotationDegrees) Px(sb, "rotation", s.RotationDegrees);
        if (s.Scale != d.Scale) Px(sb, "scale", s.Scale);

        if (s.HasBoxShadow != d.HasBoxShadow || s.HasBoxShadow)
        {
            if (s.HasBoxShadow)
                Kv(sb, "box-shadow", $"{F(s.BoxShadowOffsetX)} {F(s.BoxShadowOffsetY)} {F(s.BoxShadowBlur)} {F(s.BoxShadowSpread)} {CssColor(s.BoxShadowColor)}");
            else if (d.HasBoxShadow)
                Kv(sb, "box-shadow", "none");
        }

        if (s.HasTextShadow != d.HasTextShadow || s.HasTextShadow)
        {
            if (s.HasTextShadow)
                Kv(sb, "text-shadow", $"{F(s.TextShadowOffsetX)} {F(s.TextShadowOffsetY)} {F(s.TextShadowBlur)} {CssColor(s.TextShadowColor)}");
            else if (d.HasTextShadow)
                Kv(sb, "text-shadow", "none");
        }

        if (s.BackdropBlur != d.BackdropBlur) Kv(sb, "backdrop-filter", s.BackdropBlur > 0 ? $"blur({F(s.BackdropBlur)}px)" : "none");

        return sb.Length == 0 ? null : sb.ToString();
    }

    static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    static string CssColor(Color c) => $"rgba({(int)(c.R * 255)},{(int)(c.G * 255)},{(int)(c.B * 255)},{c.A.ToString("0.###", CultureInfo.InvariantCulture)})";

    static void EmitEdge(StringBuilder sb, string key, Style s, Style d, Func<Style, Edge, float> get)
    {
        float sl = Nz(get(s, Edge.Left)), st = Nz(get(s, Edge.Top)), sr = Nz(get(s, Edge.Right)), sb2 = Nz(get(s, Edge.Bottom));
        float dl = Nz(get(d, Edge.Left)), dt = Nz(get(d, Edge.Top)), dr = Nz(get(d, Edge.Right)), db = Nz(get(d, Edge.Bottom));
        if (sl == dl && st == dt && sr == dr && sb2 == db) return;
        Kv(sb, key, $"{F(st)} {F(sr)} {F(sb2)} {F(sl)}");
    }
    static float Nz(float v) => float.IsNaN(v) ? 0f : v;

    static void EmitInset(StringBuilder sb, string k, float v, float dv)
    {
        if (FloatEq(v, dv) || float.IsNaN(v)) return;
        Px(sb, k, v);
    }

    static string CssAlign(TextAlign a) => a switch
    {
        TextAlign.UpperLeft => "upper-left", TextAlign.UpperCenter => "upper-center", TextAlign.UpperRight => "upper-right",
        TextAlign.MiddleLeft => "middle-left", TextAlign.MiddleCenter => "middle-center", TextAlign.MiddleRight => "middle-right",
        TextAlign.LowerLeft => "lower-left", TextAlign.LowerCenter => "lower-center", _ => "lower-right",
    };

    static bool FloatEq(float a, float b) => (float.IsNaN(a) && float.IsNaN(b)) || a == b;

    static void Kv(StringBuilder sb, string k, string v) => sb.Append(k).Append(':').Append(v).Append(';');
    static void Px(StringBuilder sb, string k, float v) => Kv(sb, k, v.ToString("0.###", CultureInfo.InvariantCulture));
    static void Len(StringBuilder sb, string k, Length l)
    {
        string v = l.Unit switch
        {
            Length.Kind.Percent => l.Value.ToString("0.###", CultureInfo.InvariantCulture) + "%",
            Length.Kind.Points => l.Value.ToString("0.###", CultureInfo.InvariantCulture),
            _ => "auto",
        };
        Kv(sb, k, v);
    }
    static void Col(StringBuilder sb, string k, Color c) =>
        Kv(sb, k, $"rgba({(int)(c.R * 255)},{(int)(c.G * 255)},{(int)(c.B * 255)},{c.A.ToString("0.###", CultureInfo.InvariantCulture)})");

    static string Css(FlexDirection v) => v switch { FlexDirection.Row => "row", FlexDirection.RowReverse => "row-reverse", FlexDirection.Column => "column", _ => "column-reverse" };
    static string Css(FlexWrap v) => v switch { FlexWrap.Wrap => "wrap", FlexWrap.WrapReverse => "wrap-reverse", _ => "nowrap" };
    static string Css(Justify v) => v switch { Justify.Center => "center", Justify.FlexEnd => "flex-end", Justify.SpaceBetween => "space-between", Justify.SpaceAround => "space-around", Justify.SpaceEvenly => "space-evenly", _ => "flex-start" };
    static string Css(Align v) => v switch { Align.Auto => "auto", Align.Center => "center", Align.FlexEnd => "flex-end", Align.Stretch => "stretch", Align.Baseline => "baseline", Align.SpaceBetween => "space-between", Align.SpaceAround => "space-around", Align.SpaceEvenly => "space-evenly", _ => "flex-start" };
    static string Css(PositionType v) => v switch { PositionType.Absolute => "absolute", PositionType.Static => "static", _ => "relative" };
    static string Css(Overflow v) => v switch { Overflow.Hidden => "hidden", Overflow.Scroll => "scroll", _ => "visible" };
}
