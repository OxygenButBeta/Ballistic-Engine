using System.Globalization;
using System.Text;

namespace BallisticEngine.UI;

// Serializes the subset of a Style that differs from defaults into a CSS declaration block, so the
// resolver can re-apply imperative (code/control) overrides on top of a USS cascade (Unity element.style
// parity — see Style.CaptureImperativeOverrides). Round-trips through StyleApplier, so every property
// listed here must be BOTH serializable here AND parseable there. Layout + common visual props are
// covered (what controls/game code actually set imperatively); exotic ones (transform/text-shadow) are
// rare as imperative overrides and can be added if needed.
static class StyleSerialize
{
    static readonly Style Defaults = MakeDefaults();
    static Style MakeDefaults() { var p = new Panel(); return p.Style; } // fresh element = default style

    public static string DiffFromDefaults(Style s)
    {
        var sb = new StringBuilder();
        var d = Defaults;

        // sizing
        if (!s.Width.Equals(d.Width)) Len(sb, "width", s.Width);
        if (!s.Height.Equals(d.Height)) Len(sb, "height", s.Height);
        if (s.MinWidth != d.MinWidth) Px(sb, "min-width", s.MinWidth);
        if (s.MinHeight != d.MinHeight) Px(sb, "min-height", s.MinHeight);
        if (!FloatEq(s.MaxWidth, d.MaxWidth)) Px(sb, "max-width", s.MaxWidth);
        if (!FloatEq(s.MaxHeight, d.MaxHeight)) Px(sb, "max-height", s.MaxHeight);

        // flex
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

        // visual
        if (!s.BackgroundColor.Equals(d.BackgroundColor)) Col(sb, "background-color", s.BackgroundColor);
        if (!s.TextColor.Equals(d.TextColor)) Col(sb, "color", s.TextColor);
        if (!s.BorderColor.Equals(d.BorderColor)) Col(sb, "border-color", s.BorderColor);
        if (s.FontSize != d.FontSize) Px(sb, "font-size", s.FontSize);
        if (s.Opacity != d.Opacity) Px(sb, "opacity", s.Opacity);
        if (s.BorderRadiusTopLeft != d.BorderRadiusTopLeft) Px(sb, "border-radius", s.BorderRadiusTopLeft);
        if (s.BorderWidthVisual != d.BorderWidthVisual) Px(sb, "border-width", s.BorderWidthVisual);
        if (s.WhiteSpace != d.WhiteSpace) Kv(sb, "white-space", s.WhiteSpace == WhiteSpace.Normal ? "normal" : "nowrap");

        return sb.Length == 0 ? null : sb.ToString();
    }

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
