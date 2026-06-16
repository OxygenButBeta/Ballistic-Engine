using System;
using System.Globalization;

namespace BallisticEngine.UI;

// Translates CSS-style declarations ("property: value") into Style setters. This is the single
// shared bridge between authored text (inline style="" attributes AND .uss rules) and the live
// Style object, so both routes support the exact same property vocabulary.
//
// The vocabulary is intentionally CSS-named and broad enough to cover what Claude designs emit:
// flex layout, the box model (margin/padding/border/inset, with 1/2/4-value shorthands), sizing
// (px/%/auto), colors (hex, rgb(), rgba(), named basics), border-radius, font-size, opacity, and
// text-align. Unknown properties are ignored (logged) rather than fatal — a port should load even
// when the source uses a property we don't model yet.
//
// Numbers are parsed with InvariantCulture ALWAYS (the port skill's hard-won locale rule: a Turkish/
// EU locale otherwise reads "2.4" as a thousands separator and mangles every value).
public static class StyleApplier
{
    // Apply a full inline declaration block: "a: b; c: d; ...". Used for style="" and as the per-rule
    // applier for USS.
    public static void ApplyInline(Style style, string declarations)
    {
        if (string.IsNullOrWhiteSpace(declarations)) return;

        foreach (var decl in declarations.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = decl.IndexOf(':');
            if (colon <= 0) continue;
            var prop = decl[..colon].Trim();
            var val = decl[(colon + 1)..].Trim();
            if (prop.Length == 0 || val.Length == 0) continue;
            ApplyOne(style, prop, val);
        }
    }

    // Apply a single property/value. Public so the USS cascade can feed pre-split declarations.
    public static void ApplyOne(Style style, string prop, string value)
    {
        switch (prop.ToLowerInvariant())
        {
            // ---- flex container ----
            case "flex-direction": style.FlexDirection = ParseFlexDirection(value); break;
            case "flex-wrap": style.FlexWrap = ParseFlexWrap(value); break;
            case "justify-content": style.JustifyContent = ParseJustify(value); break;
            case "align-items": style.AlignItems = ParseAlign(value); break;
            case "align-content": style.AlignContent = ParseAlign(value); break;
            case "align-self": style.AlignSelf = ParseAlign(value); break;

            // ---- flex item ----
            case "flex-grow": style.FlexGrow = ParseFloat(value); break;
            case "flex-shrink": style.FlexShrink = ParseFloat(value); break;
            case "flex-basis": style.FlexBasis = ParseLength(value); break;
            case "flex": ApplyFlexShorthand(style, value); break;
            case "position": style.Position = ParsePosition(value); break;
            case "display": style.Display = value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) ? DisplayStyle.None : DisplayStyle.Flex; break;
            case "overflow": style.Overflow = ParseOverflow(value); break;

            // ---- sizing ----
            case "width": style.Width = ParseLength(value); break;
            case "height": style.Height = ParseLength(value); break;
            case "min-width": style.MinWidth = ParsePx(value); break;
            case "min-height": style.MinHeight = ParsePx(value); break;
            case "max-width": style.MaxWidth = ParsePx(value); break;
            case "max-height": style.MaxHeight = ParsePx(value); break;

            // ---- inset (position offsets) ----
            case "left": style.SetInset(Edge.Left, ParseLength(value)); break;
            case "top": style.SetInset(Edge.Top, ParseLength(value)); break;
            case "right": style.SetInset(Edge.Right, ParseLength(value)); break;
            case "bottom": style.SetInset(Edge.Bottom, ParseLength(value)); break;

            // ---- margin / padding (1/2/4-value shorthands + per-edge) ----
            case "margin": ApplyBoxShorthand(value, style.SetMargin); break;
            case "margin-left": style.SetMargin(Edge.Left, ParsePx(value)); break;
            case "margin-top": style.SetMargin(Edge.Top, ParsePx(value)); break;
            case "margin-right": style.SetMargin(Edge.Right, ParsePx(value)); break;
            case "margin-bottom": style.SetMargin(Edge.Bottom, ParsePx(value)); break;

            case "padding": ApplyBoxShorthand(value, style.SetPadding); break;
            case "padding-left": style.SetPadding(Edge.Left, ParsePx(value)); break;
            case "padding-top": style.SetPadding(Edge.Top, ParsePx(value)); break;
            case "padding-right": style.SetPadding(Edge.Right, ParsePx(value)); break;
            case "padding-bottom": style.SetPadding(Edge.Bottom, ParsePx(value)); break;

            // ---- border (width + color + radius) ----
            case "border-width": style.SetBorderWidth(Edge.All, ParsePx(value)); break;
            case "border-color": style.BorderColor = ParseColor(value); break;
            case "border-radius": ApplyRadiusShorthand(style, value); break;
            case "border-top-left-radius": style.BorderRadiusTopLeft = ParsePx(value); break;
            case "border-top-right-radius": style.BorderRadiusTopRight = ParsePx(value); break;
            case "border-bottom-right-radius": style.BorderRadiusBottomRight = ParsePx(value); break;
            case "border-bottom-left-radius": style.BorderRadiusBottomLeft = ParsePx(value); break;

            // ---- visual ----
            case "background-color": style.BackgroundColor = ParseColor(value); break;
            case "background":
                // CSS `background` shorthand: a gradient, or a solid color (we only model those two).
                if (value.Contains("gradient", StringComparison.OrdinalIgnoreCase))
                    style.BackgroundGradient = ParseGradient(value);
                else
                    style.BackgroundColor = ParseColor(value);
                break;
            case "color": style.TextColor = ParseColor(value); break;
            case "font-size": style.FontSize = ParsePx(value); break;
            case "opacity": style.Opacity = Math.Clamp(ParseFloat(value), 0f, 1f); break;
            // Render-time transforms (don't affect layout). rotation in degrees, scale unitless,
            // translate-x/y in px. (CSS `transform:` shorthand is parsed loosely below.)
            case "rotation": style.RotationDegrees = ParseFloat(value.Replace("deg", "")); break;
            case "scale": style.Scale = ParseFloat(value); break;
            case "translate-x": style.TranslateX = ParsePx(value); break;
            case "translate-y": style.TranslateY = ParsePx(value); break;
            case "font-family": style.FontFamily = ParseFontFamily(value); break;
            case "text-shadow": ApplyTextShadow(style, value); break;
            // letter-spacing in px or em (em resolved against the current font size, like CSS).
            case "letter-spacing": style.LetterSpacing = ParseEmOrPx(value, style.FontSize); break;
            case "text-align":
            case "-unity-text-align": style.TextAlign = ParseTextAlign(value); break;

            default:
                // Unmodeled property (e.g. box-shadow, transition) — skip without failing the load.
                break;
        }
    }

    // ---------------------------------------------------------------- value parsers

    // A CSS length: "auto", "50%", or a pixel number ("12px" / "12"). Unknown -> auto.
    static Length ParseLength(string v)
    {
        v = v.Trim();
        if (v.Equals("auto", StringComparison.OrdinalIgnoreCase)) return Length.Auto;
        if (v.EndsWith("%"))
            return float.TryParse(v[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? Length.Percent(p) : Length.Auto;
        return Length.Points(ParsePx(v));
    }

    // A pixel scalar: strips a trailing "px", parses the number invariantly. Non-numeric -> 0.
    static float ParsePx(string v)
    {
        v = v.Trim();
        if (v.EndsWith("px", StringComparison.OrdinalIgnoreCase)) v = v[..^2].Trim();
        return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0f;
    }

    static float ParseFloat(string v) =>
        float.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0f;

    // text-shadow: "offsetX offsetY blur color". CSS allows a comma list of shadows; we pick the one
    // with the LARGEST blur (the glow, the visually dominant layer) since the renderer draws a single
    // shadow pass in v1. "none" clears it.
    static void ApplyTextShadow(Style style, string value)
    {
        if (value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            style.HasTextShadow = false;
            return;
        }

        float bestBlur = -1f;
        foreach (var shadow in SplitTopLevel(value))
        {
            if (!TryParseOneShadow(shadow.Trim(), out float ox, out float oy, out float blur, out Color col))
                continue;
            if (blur > bestBlur)
            {
                bestBlur = blur;
                style.HasTextShadow = true;
                style.TextShadowOffsetX = ox;
                style.TextShadowOffsetY = oy;
                style.TextShadowBlur = blur;
                style.TextShadowColor = col;
            }
        }
    }

    static bool TryParseOneShadow(string s, out float ox, out float oy, out float blur, out Color col)
    {
        ox = oy = blur = 0f; col = Color.Transparent;
        // Pull the color out first (it may contain spaces inside rgba()).
        int rgb = s.IndexOf("rgb", StringComparison.OrdinalIgnoreCase);
        int hash = s.IndexOf('#');
        int colorStart = rgb >= 0 ? rgb : hash;
        string lengths;
        if (colorStart >= 0)
        {
            col = ParseColor(s[colorStart..].Trim());
            lengths = s[..colorStart];
        }
        else lengths = s;

        var nums = lengths.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (nums.Length < 2) return false;
        ox = ParsePx(nums[0]);
        oy = ParsePx(nums[1]);
        if (nums.Length >= 3) blur = ParsePx(nums[2]);
        return true;
    }

    // text-align: accepts CSS keywords (left/center/right -> middle-*) and Unity's compound names
    // (upper-left, middle-center, lower-right, ...).
    static TextAlign ParseTextAlign(string v) => v.Trim().ToLowerInvariant() switch
    {
        "left" or "middle-left" => TextAlign.MiddleLeft,
        "center" or "middle-center" => TextAlign.MiddleCenter,
        "right" or "middle-right" => TextAlign.MiddleRight,
        "upper-left" or "top-left" => TextAlign.UpperLeft,
        "upper-center" or "top-center" => TextAlign.UpperCenter,
        "upper-right" or "top-right" => TextAlign.UpperRight,
        "lower-left" or "bottom-left" => TextAlign.LowerLeft,
        "lower-center" or "bottom-center" => TextAlign.LowerCenter,
        "lower-right" or "bottom-right" => TextAlign.LowerRight,
        _ => TextAlign.MiddleLeft,
    };

    // font-family: takes the FIRST family in a comma list, stripped of quotes (CSS fallback lists like
    // "'Cinzel', serif" -> "Cinzel").
    static string ParseFontFamily(string v)
    {
        var first = v.Split(',')[0].Trim();
        return first.Trim('\'', '"', ' ');
    }

    // A length that may be em (relative to font size) or px. "0.42em" * fontSize, "5px" -> 5.
    static float ParseEmOrPx(string v, float fontSize)
    {
        v = v.Trim();
        if (v.EndsWith("em", StringComparison.OrdinalIgnoreCase))
            return ParseFloat(v[..^2]) * (fontSize <= 0 ? 14f : fontSize);
        return ParsePx(v);
    }

    // Color: #hex (3/4/6/8), rgb()/rgba(), or a small set of CSS named colors. Unknown -> transparent.
    static Color ParseColor(string v)
    {
        v = v.Trim();
        if (v.StartsWith("#")) return Color.FromHex(v);

        if (v.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            int open = v.IndexOf('('), close = v.IndexOf(')');
            if (open >= 0 && close > open)
            {
                var parts = v[(open + 1)..close].Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    byte r = (byte)Math.Clamp(ParseFloat(parts[0]), 0, 255);
                    byte g = (byte)Math.Clamp(ParseFloat(parts[1]), 0, 255);
                    byte b = (byte)Math.Clamp(ParseFloat(parts[2]), 0, 255);
                    float a = parts.Length >= 4 ? Math.Clamp(ParseFloat(parts[3]), 0f, 1f) : 1f;
                    return Color.Rgba(r, g, b, a);
                }
            }
            return Color.Transparent;
        }

        return v.ToLowerInvariant() switch
        {
            "white" => Color.White,
            "black" => Color.Black,
            "transparent" or "none" => Color.Transparent,
            "red" => Color.Rgb(255, 0, 0),
            "green" => Color.Rgb(0, 128, 0),
            "blue" => Color.Rgb(0, 0, 255),
            "gray" or "grey" => Color.Rgb(128, 128, 128),
            _ => Color.Transparent,
        };
    }

    // Parses `linear-gradient(<angle>, <stop>, ...)` or `radial-gradient([shape] [at pos], <stop>...)`.
    // Each stop is "<color> [position%]". Positions default to an even spread when omitted. Returns
    // null on malformed input (caller leaves the solid background).
    static Gradient ParseGradient(string value)
    {
        value = value.Trim();
        bool radial = value.StartsWith("radial", StringComparison.OrdinalIgnoreCase);
        int open = value.IndexOf('(');
        int close = value.LastIndexOf(')');
        if (open < 0 || close <= open) return null;
        string inner = value[(open + 1)..close];

        var parts = SplitTopLevel(inner);
        if (parts.Count == 0) return null;

        var g = new Gradient { Type = radial ? Gradient.Kind.Radial : Gradient.Kind.Linear };
        int firstStop = 0;

        if (!radial)
        {
            // Optional leading angle ("90deg" / "to right"). If the first token isn't a color, treat
            // it as the direction.
            var head = parts[0].Trim();
            if (head.EndsWith("deg", StringComparison.OrdinalIgnoreCase))
            {
                g.AngleDegrees = ParseFloat(head[..^3]);
                firstStop = 1;
            }
            else if (head.StartsWith("to ", StringComparison.OrdinalIgnoreCase))
            {
                g.AngleDegrees = head.ToLowerInvariant() switch
                {
                    "to top" => 0f, "to right" => 90f, "to bottom" => 180f, "to left" => 270f,
                    _ => 180f,
                };
                firstStop = 1;
            }
            else
            {
                g.AngleDegrees = 180f; // CSS default: to bottom
            }
        }
        else
        {
            // Radial: skip an optional shape/size/"at pos" head token if it isn't a color.
            var head = parts[0].Trim();
            if (!LooksLikeColor(head))
            {
                ParseRadialHead(head, g);
                firstStop = 1;
            }
        }

        // Color stops. A stop is "<color> [pos%]".
        int stopCount = parts.Count - firstStop;
        for (int i = firstStop; i < parts.Count; i++)
        {
            var (color, pos) = ParseStop(parts[i].Trim(), i - firstStop, stopCount);
            g.Stops.Add(new Gradient.Stop(color, pos));
        }
        return g.Stops.Count > 0 ? g : null;
    }

    static (Color color, float pos) ParseStop(string text, int index, int total)
    {
        // Split off a trailing percentage position if present (but NOT one inside an rgba()).
        int lastSpace = LastTopLevelSpace(text);
        float pos = total > 1 ? index / (float)(total - 1) : 0f;
        string colorPart = text;
        if (lastSpace > 0)
        {
            var tail = text[(lastSpace + 1)..].Trim();
            if (tail.EndsWith("%") && float.TryParse(tail[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
            {
                pos = p / 100f;
                colorPart = text[..lastSpace].Trim();
            }
        }
        return (ParseColor(colorPart), pos);
    }

    static void ParseRadialHead(string head, Gradient g)
    {
        // e.g. "ellipse 74% 62% at 34% 50%". Pull the "at X% Y%" center; sizes map to radii.
        int at = head.IndexOf(" at ", StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            var posPart = head[(at + 4)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (posPart.Length >= 1) g.CenterX = Percent01(posPart[0], 0.5f);
            if (posPart.Length >= 2) g.CenterY = Percent01(posPart[1], 0.5f);
            head = head[..at];
        }
        var sizes = head.Replace("ellipse", "").Replace("circle", "").Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (sizes.Length >= 1) g.RadiusX = Percent01(sizes[0], 0.5f);
        if (sizes.Length >= 2) g.RadiusY = Percent01(sizes[1], 0.5f);
    }

    static float Percent01(string s, float fallback) =>
        s.EndsWith("%") && float.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v / 100f : fallback;

    static bool LooksLikeColor(string s) =>
        s.StartsWith("#") || s.StartsWith("rgb", StringComparison.OrdinalIgnoreCase) ||
        s is "white" or "black" or "transparent" or "red" or "green" or "blue" or "gray" or "grey";

    // Splits on top-level commas (commas inside parentheses, e.g. rgba(...), are preserved).
    static List<string> SplitTopLevel(string s)
    {
        var result = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0) { result.Add(s[start..i]); start = i + 1; }
        }
        if (start < s.Length) result.Add(s[start..]);
        return result;
    }

    // Index of the last space that is NOT inside parentheses (separates a stop's color from its pos).
    static int LastTopLevelSpace(string s)
    {
        int depth = 0, last = -1;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ' ' && depth == 0) last = i;
        }
        return last;
    }

    // ---------------------------------------------------------------- enum parsers (CSS keyword set)

    static FlexDirection ParseFlexDirection(string v) => v.Trim().ToLowerInvariant() switch
    {
        "row" => FlexDirection.Row,
        "row-reverse" => FlexDirection.RowReverse,
        "column" => FlexDirection.Column,
        "column-reverse" => FlexDirection.ColumnReverse,
        _ => FlexDirection.Row,
    };

    static FlexWrap ParseFlexWrap(string v) => v.Trim().ToLowerInvariant() switch
    {
        "wrap" => FlexWrap.Wrap,
        "wrap-reverse" => FlexWrap.WrapReverse,
        _ => FlexWrap.NoWrap,
    };

    static Justify ParseJustify(string v) => v.Trim().ToLowerInvariant() switch
    {
        "center" => Justify.Center,
        "flex-end" or "end" => Justify.FlexEnd,
        "space-between" => Justify.SpaceBetween,
        "space-around" => Justify.SpaceAround,
        "space-evenly" => Justify.SpaceEvenly,
        _ => Justify.FlexStart,
    };

    static Align ParseAlign(string v) => v.Trim().ToLowerInvariant() switch
    {
        "auto" => Align.Auto,
        "center" => Align.Center,
        "flex-end" or "end" => Align.FlexEnd,
        "stretch" => Align.Stretch,
        "baseline" => Align.Baseline,
        "space-between" => Align.SpaceBetween,
        "space-around" => Align.SpaceAround,
        "space-evenly" => Align.SpaceEvenly,
        "flex-start" or "start" => Align.FlexStart,
        _ => Align.Stretch,
    };

    static PositionType ParsePosition(string v) => v.Trim().ToLowerInvariant() switch
    {
        "absolute" => PositionType.Absolute,
        "static" => PositionType.Static,
        // CSS "fixed"/"sticky" have no flex analogue; treat as absolute (closest behaviour).
        "fixed" or "sticky" => PositionType.Absolute,
        _ => PositionType.Relative,
    };

    static Overflow ParseOverflow(string v) => v.Trim().ToLowerInvariant() switch
    {
        "hidden" => Overflow.Hidden,
        "scroll" or "auto" => Overflow.Scroll,
        _ => Overflow.Visible,
    };

    // ---------------------------------------------------------------- shorthands

    // "flex: 1" -> grow 1; "flex: 1 1 0" -> grow shrink basis. Partial forms handled leniently.
    static void ApplyFlexShorthand(Style style, string v)
    {
        var parts = v.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 1) style.FlexGrow = ParseFloat(parts[0]);
        if (parts.Length >= 2) style.FlexShrink = ParseFloat(parts[1]);
        if (parts.Length >= 3) style.FlexBasis = ParseLength(parts[2]);
    }

    // CSS box shorthand: 1 value = all; 2 = vertical horizontal; 4 = top right bottom left.
    static void ApplyBoxShorthand(string v, Action<Edge, float> set)
    {
        var parts = v.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts.Length)
        {
            case 1:
                set(Edge.All, ParsePx(parts[0]));
                break;
            case 2:
                set(Edge.Vertical, ParsePx(parts[0]));
                set(Edge.Horizontal, ParsePx(parts[1]));
                break;
            case 3: // top, horizontal, bottom
                set(Edge.Top, ParsePx(parts[0]));
                set(Edge.Horizontal, ParsePx(parts[1]));
                set(Edge.Bottom, ParsePx(parts[2]));
                break;
            case 4:
                set(Edge.Top, ParsePx(parts[0]));
                set(Edge.Right, ParsePx(parts[1]));
                set(Edge.Bottom, ParsePx(parts[2]));
                set(Edge.Left, ParsePx(parts[3]));
                break;
        }
    }

    // border-radius shorthand: 1 value = all corners; 4 = TL TR BR BL.
    static void ApplyRadiusShorthand(Style style, string v)
    {
        var parts = v.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1) { style.BorderRadius = ParsePx(parts[0]); return; }
        if (parts.Length >= 4)
        {
            style.BorderRadiusTopLeft = ParsePx(parts[0]);
            style.BorderRadiusTopRight = ParsePx(parts[1]);
            style.BorderRadiusBottomRight = ParsePx(parts[2]);
            style.BorderRadiusBottomLeft = ParsePx(parts[3]);
        }
    }
}
