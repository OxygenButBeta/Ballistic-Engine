using System.Globalization;

namespace BallisticEngine.UI;

public static class StyleApplier
{
    public enum Pass { Normal, Important, All }

    public static void ApplyInline(Style style, string declarations, Pass pass = Pass.All, IVarResolver vars = null)
    {
        if (string.IsNullOrWhiteSpace(declarations)) return;

        foreach (var decl in declarations.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = decl.IndexOf(':');
            if (colon <= 0) continue;
            var prop = decl[..colon].Trim();
            var val = decl[(colon + 1)..].Trim();
            if (prop.Length == 0 || val.Length == 0) continue;

            bool important = false;
            int bang = val.IndexOf('!');
            if (bang >= 0 && val[bang..].Replace(" ", "").Equals("!important", StringComparison.OrdinalIgnoreCase))
            {
                important = true;
                val = val[..bang].Trim();
            }
            if (pass == Pass.Normal && important) continue;
            if (pass == Pass.Important && !important) continue;

            if (vars != null && val.Contains("var(", StringComparison.OrdinalIgnoreCase))
                val = ResolveVars(val, vars);
            if (val.Length == 0) continue;

            ApplyOne(style, prop, val);
        }
    }

    static string ResolveVars(string val, IVarResolver vars)
    {
        int guard = 0;
        while (true)
        {
            int v = val.IndexOf("var(", StringComparison.OrdinalIgnoreCase);
            if (v < 0 || guard++ > 32) break;
            int close = MatchParen(val, v + 3);
            if (close < 0) break;
            string inner = val[(v + 4)..close];
            string name, fallback = "";
            int comma = TopLevelComma(inner);
            if (comma >= 0) { name = inner[..comma].Trim(); fallback = inner[(comma + 1)..].Trim(); }
            else name = inner.Trim();
            string resolved = vars.ResolveVar(name);
            if (string.IsNullOrEmpty(resolved)) resolved = fallback;
            val = val[..v] + resolved + val[(close + 1)..];
        }
        return val.Trim();
    }

    static int MatchParen(string s, int openIdx)
    {
        int depth = 0;
        for (int i = openIdx; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')') { depth--; if (depth == 0) return i; }
        }
        return -1;
    }

    static int TopLevelComma(string s)
    {
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')') depth--;
            else if (s[i] == ',' && depth == 0) return i;
        }
        return -1;
    }

    public static void ApplyOne(Style style, string prop, string value)
    {
        switch (prop.ToLowerInvariant())
        {
            case "flex-direction": style.FlexDirection = ParseFlexDirection(value); break;
            case "flex-wrap": style.FlexWrap = ParseFlexWrap(value); break;
            case "justify-content": style.JustifyContent = ParseJustify(value); break;
            case "align-items": style.AlignItems = ParseAlign(value); break;
            case "align-content": style.AlignContent = ParseAlign(value); break;
            case "align-self": style.AlignSelf = ParseAlign(value); break;

            case "flex-grow": style.FlexGrow = ParseFloat(value); break;
            case "flex-shrink": style.FlexShrink = ParseFloat(value); break;
            case "flex-basis": style.FlexBasis = ParseLength(value); break;
            case "flex": ApplyFlexShorthand(style, value); break;
            case "position": style.Position = ParsePosition(value); break;
            case "display": style.Display = value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) ? DisplayStyle.None : DisplayStyle.Flex; break;
            case "overflow": style.Overflow = ParseOverflow(value); break;
            case "gap": style.Gap = ParsePx(value); break;
            case "row-gap": style.RowGap = ParsePx(value); break;
            case "column-gap": style.ColumnGap = ParsePx(value); break;
            case "aspect-ratio": style.AspectRatio = ParseAspectRatio(value); break;
            case "white-space":
                style.WhiteSpace = value.Trim().Equals("nowrap", StringComparison.OrdinalIgnoreCase)
                    ? WhiteSpace.NoWrap : WhiteSpace.Normal; break;
            case "text-overflow":
                style.TextOverflow = value.Trim().Equals("ellipsis", StringComparison.OrdinalIgnoreCase)
                    ? TextOverflow.Ellipsis : TextOverflow.Clip; break;

            case "width": style.Width = ParseLength(value); break;
            case "height": style.Height = ParseLength(value); break;
            case "min-width": style.MinWidth = NoneOrPx(value, 0f); break;
            case "min-height": style.MinHeight = NoneOrPx(value, 0f); break;
            case "max-width": style.MaxWidth = NoneOrPx(value, float.NaN); break;
            case "max-height": style.MaxHeight = NoneOrPx(value, float.NaN); break;

            case "left": style.SetInset(Edge.Left, ParseLength(value)); break;
            case "top": style.SetInset(Edge.Top, ParseLength(value)); break;
            case "right": style.SetInset(Edge.Right, ParseLength(value)); break;
            case "bottom": style.SetInset(Edge.Bottom, ParseLength(value)); break;

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

            case "border-width": style.SetBorderWidth(Edge.All, ParsePx(value)); break;
            case "border-color": style.BorderColor = ParseColor(value); break;
            case "border-radius": ApplyRadiusShorthand(style, value); break;
            case "border-top-left-radius": style.BorderRadiusTopLeft = ParsePx(value); break;
            case "border-top-right-radius": style.BorderRadiusTopRight = ParsePx(value); break;
            case "border-bottom-right-radius": style.BorderRadiusBottomRight = ParsePx(value); break;
            case "border-bottom-left-radius": style.BorderRadiusBottomLeft = ParsePx(value); break;

            case "background-color": style.BackgroundColor = ParseColor(value); break;
            case "background":
                if (value.Contains("gradient", StringComparison.OrdinalIgnoreCase))
                    style.BackgroundGradient = ParseGradient(value);
                else
                    style.BackgroundColor = ParseColor(value);
                break;
            case "color": style.TextColor = ParseColor(value); break;
            case "font-size": style.FontSize = ParsePx(value); break;
            case "opacity": style.Opacity = Math.Clamp(ParseFloat(value), 0f, 1f); break;
            case "rotation": style.RotationDegrees = ParseFloat(value.Replace("deg", "")); break;
            case "scale": style.Scale = ParseFloat(value); break;
            case "translate-x": style.TranslateX = ParsePx(value); break;
            case "translate-y": style.TranslateY = ParsePx(value); break;
            case "font-family": style.FontFamily = ParseFontFamily(value); break;
            case "text-shadow": ApplyTextShadow(style, value); break;
            case "box-shadow": ApplyBoxShadow(style, value); break;
            case "backdrop-filter":
            case "-webkit-backdrop-filter": style.BackdropBlur = ParseBlurPx(value); break;
            case "font-weight":
                style.Bold = value.Trim() is "bold" or "700" or "800" or "900"
                    || (int.TryParse(value.Trim(), out var fw) && fw >= 600); break;
            case "font-style": style.Italic = value.Trim().Equals("italic", StringComparison.OrdinalIgnoreCase); break;
            case "direction": style.Direction = value.Trim().Equals("rtl", StringComparison.OrdinalIgnoreCase) ? LayoutDirection.RTL : LayoutDirection.LTR; break;
            case "letter-spacing": style.LetterSpacing = ParseEmOrPx(value, style.FontSize); break;
            case "text-align":
            case "-unity-text-align": style.TextAlign = ParseTextAlign(value); break;

            default:
                break;
        }
    }

    static Length ParseLength(string v)
    {
        v = v.Trim();
        if (v.Equals("auto", StringComparison.OrdinalIgnoreCase)) return Length.Auto;
        if (v.EndsWith("%"))
            return float.TryParse(v[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? Length.Percent(p) : Length.Auto;
        return Length.Points(ParsePx(v));
    }

    static float ParsePx(string v)
    {
        v = v.Trim();
        if (v.EndsWith("px", StringComparison.OrdinalIgnoreCase)) v = v[..^2].Trim();
        return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0f;
    }

    static float ParseFloat(string v) =>
        float.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0f;

    static float NoneOrPx(string v, float none) =>
        v.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) ? none : ParsePx(v);

    static float ParseAspectRatio(string v)
    {
        v = v.Trim();
        if (v.Equals("auto", StringComparison.OrdinalIgnoreCase)) return float.NaN;
        int slash = v.IndexOf('/');
        if (slash > 0)
        {
            float w = ParseFloat(v[..slash]);
            float h = ParseFloat(v[(slash + 1)..]);
            return h != 0 ? w / h : float.NaN;
        }
        float r = ParseFloat(v);
        return r > 0 ? r : float.NaN;
    }

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

    static void ApplyBoxShadow(Style style, string value)
    {
        if (value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) { style.HasBoxShadow = false; return; }
        var first = SplitTopLevel(value)[0].Trim().Replace("inset", "").Trim();
        int rgb = first.IndexOf("rgb", StringComparison.OrdinalIgnoreCase);
        int hash = first.IndexOf('#');
        int colorStart = rgb >= 0 ? rgb : hash;
        Color col = Color.Rgba(0, 0, 0, 0.4f);
        string lengths = first;
        if (colorStart >= 0) { col = ParseColor(first[colorStart..].Trim()); lengths = first[..colorStart]; }
        var nums = lengths.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (nums.Length < 2) return;
        style.HasBoxShadow = true;
        style.BoxShadowOffsetX = ParsePx(nums[0]);
        style.BoxShadowOffsetY = ParsePx(nums[1]);
        style.BoxShadowBlur = nums.Length >= 3 ? ParsePx(nums[2]) : 0f;
        style.BoxShadowSpread = nums.Length >= 4 ? ParsePx(nums[3]) : 0f;
        style.BoxShadowColor = col;
    }

    static float ParseBlurPx(string v)
    {
        v = v.Trim();
        int open = v.IndexOf('('), close = v.IndexOf(')');
        if (open >= 0 && close > open) return ParsePx(v[(open + 1)..close]);
        return ParsePx(v);
    }

    static bool TryParseOneShadow(string s, out float ox, out float oy, out float blur, out Color col)
    {
        ox = oy = blur = 0f; col = Color.Transparent;
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

    static string ParseFontFamily(string v)
    {
        var first = v.Split(',')[0].Trim();
        return first.Trim('\'', '"', ' ');
    }

    static float ParseEmOrPx(string v, float fontSize)
    {
        v = v.Trim();
        if (v.EndsWith("em", StringComparison.OrdinalIgnoreCase))
            return ParseFloat(v[..^2]) * (fontSize <= 0 ? 14f : fontSize);
        return ParsePx(v);
    }

    static Color ParseColor(string v)
    {
        v = v.Trim();
        if (v.StartsWith("#")) return Color.FromHex(v);

        if (v.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            int open = v.IndexOf('('), close = v.IndexOf(')');
            if (open >= 0 && close > open)
            {
                var body = v[(open + 1)..close].Replace("/", " ");
                var parts = body.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    byte r = ParseChannel(parts[0]);
                    byte g = ParseChannel(parts[1]);
                    byte b = ParseChannel(parts[2]);
                    float a = parts.Length >= 4 ? ParseAlpha(parts[3]) : 1f;
                    return Color.Rgba(r, g, b, a);
                }
            }
            return Color.Transparent;
        }

        if (v.StartsWith("hsl", StringComparison.OrdinalIgnoreCase))
        {
            int open = v.IndexOf('('), close = v.IndexOf(')');
            if (open >= 0 && close > open)
            {
                var body = v[(open + 1)..close].Replace("/", " ");
                var parts = body.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    float h = ParseFloat(parts[0].Replace("deg", ""));
                    float s = ParsePercentOrFloat(parts[1]);
                    float l = ParsePercentOrFloat(parts[2]);
                    float a = parts.Length >= 4 ? ParseAlpha(parts[3]) : 1f;
                    return HslToColor(h, s, l, a);
                }
            }
            return Color.Transparent;
        }

        return NamedColor(v);
    }

    static byte ParseChannel(string p)
    {
        p = p.Trim();
        if (p.EndsWith("%"))
            return (byte)Math.Clamp(ParseFloat(p[..^1]) / 100f * 255f, 0, 255);
        return (byte)Math.Clamp(ParseFloat(p), 0, 255);
    }

    static float ParseAlpha(string p)
    {
        p = p.Trim();
        if (p.EndsWith("%")) return Math.Clamp(ParseFloat(p[..^1]) / 100f, 0f, 1f);
        return Math.Clamp(ParseFloat(p), 0f, 1f);
    }

    static float ParsePercentOrFloat(string p)
    {
        p = p.Trim();
        return p.EndsWith("%") ? ParseFloat(p[..^1]) / 100f : ParseFloat(p);
    }

    static Color HslToColor(float hDeg, float s, float l, float a)
    {
        float h = ((hDeg % 360f) + 360f) % 360f / 360f;
        float q = l < 0.5f ? l * (1f + s) : l + s - l * s;
        float p = 2f * l - q;
        float r = HueToRgb(p, q, h + 1f / 3f);
        float g = HueToRgb(p, q, h);
        float b = HueToRgb(p, q, h - 1f / 3f);
        return new Color(r, g, b, a);
    }

    static float HueToRgb(float p, float q, float t)
    {
        if (t < 0f) t += 1f;
        if (t > 1f) t -= 1f;
        if (t < 1f / 6f) return p + (q - p) * 6f * t;
        if (t < 1f / 2f) return q;
        if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
        return p;
    }

    static Color NamedColor(string v) => v.ToLowerInvariant() switch
    {
        "white" => Color.White,
        "black" => Color.Black,
        "transparent" or "none" => Color.Transparent,
        "red" => Color.Rgb(255, 0, 0),
        "green" => Color.Rgb(0, 128, 0),
        "lime" => Color.Rgb(0, 255, 0),
        "blue" => Color.Rgb(0, 0, 255),
        "yellow" => Color.Rgb(255, 255, 0),
        "cyan" or "aqua" => Color.Rgb(0, 255, 255),
        "magenta" or "fuchsia" => Color.Rgb(255, 0, 255),
        "orange" => Color.Rgb(255, 165, 0),
        "purple" => Color.Rgb(128, 0, 128),
        "gray" or "grey" => Color.Rgb(128, 128, 128),
        "silver" => Color.Rgb(192, 192, 192),
        "maroon" => Color.Rgb(128, 0, 0),
        "navy" => Color.Rgb(0, 0, 128),
        "teal" => Color.Rgb(0, 128, 128),
        "olive" => Color.Rgb(128, 128, 0),
        _ => Color.Transparent,
    };

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
                g.AngleDegrees = 180f;
            }
        }
        else
        {
            var head = parts[0].Trim();
            if (!LooksLikeColor(head))
            {
                ParseRadialHead(head, g);
                firstStop = 1;
            }
        }

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
        "fixed" or "sticky" => PositionType.Absolute,
        _ => PositionType.Relative,
    };

    static Overflow ParseOverflow(string v) => v.Trim().ToLowerInvariant() switch
    {
        "hidden" => Overflow.Hidden,
        "scroll" or "auto" => Overflow.Scroll,
        _ => Overflow.Visible,
    };

    static void ApplyFlexShorthand(Style style, string v)
    {
        var parts = v.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 1) style.FlexGrow = ParseFloat(parts[0]);
        if (parts.Length >= 2) style.FlexShrink = ParseFloat(parts[1]);
        if (parts.Length >= 3) style.FlexBasis = ParseLength(parts[2]);
    }

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
            case 3:
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

    static void ApplyRadiusShorthand(Style style, string v)
    {
        var parts = v.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts.Length)
        {
            case 1:
                style.BorderRadius = ParsePx(parts[0]);
                break;
            case 2:
            {
                float a = ParsePx(parts[0]), b = ParsePx(parts[1]);
                style.BorderRadiusTopLeft = style.BorderRadiusBottomRight = a;
                style.BorderRadiusTopRight = style.BorderRadiusBottomLeft = b;
                break;
            }
            case 3:
            {
                float a = ParsePx(parts[0]), b = ParsePx(parts[1]), c = ParsePx(parts[2]);
                style.BorderRadiusTopLeft = a;
                style.BorderRadiusTopRight = style.BorderRadiusBottomLeft = b;
                style.BorderRadiusBottomRight = c;
                break;
            }
            default:
                style.BorderRadiusTopLeft = ParsePx(parts[0]);
                style.BorderRadiusTopRight = ParsePx(parts[1]);
                style.BorderRadiusBottomRight = ParsePx(parts[2]);
                style.BorderRadiusBottomLeft = ParsePx(parts[3]);
                break;
        }
    }
}
