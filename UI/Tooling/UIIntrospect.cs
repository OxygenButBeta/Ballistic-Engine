using System.Text;

namespace BallisticEngine.UI;

// Headless UI introspection (P8.2/P8.3) — serialize a laid-out VisualElement tree to JSON: per element
// its type, name, classes, resolved box, and the key resolved-style values. This is the AI-native "or
// BETTER" edge: an agent can read a UI's structure + computed style + layout the same way `bal scene`
// reads a scene, instead of eyeballing pixels. Backs both `bal ui dump` and the in-editor UI debugger.
//
// Pure (BCL only, no GPU): call after a layout solve so ResolvedRect is valid.
public static class UIIntrospect
{
    // Serialize the tree rooted at `el` to a JSON string. `includeStyle` adds the resolved-style block.
    public static string ToJson(VisualElement el, bool includeStyle = true)
    {
        var sb = new StringBuilder(4096);
        Write(sb, el, includeStyle, 0);
        return sb.ToString();
    }

    static void Write(StringBuilder sb, VisualElement el, bool includeStyle, int depth)
    {
        sb.Append('{');
        Str(sb, "type", el.TypeName); sb.Append(',');
        Str(sb, "name", el.Name ?? ""); sb.Append(',');
        sb.Append("\"classes\":[");
        var cl = el.ClassList;
        for (int i = 0; i < cl.Count; i++) { if (i > 0) sb.Append(','); Json(sb, cl[i]); }
        sb.Append("],");

        var r = el.ResolvedRect;
        sb.Append("\"rect\":{");
        Num(sb, "x", r.X); sb.Append(','); Num(sb, "y", r.Y); sb.Append(',');
        Num(sb, "w", r.Width); sb.Append(','); Num(sb, "h", r.Height);
        sb.Append('}');

        sb.Append(",\"picking\":").Append(el.PickingEnabled ? "true" : "false");
        if (el.Focusable) sb.Append(",\"focusable\":true");

        if (includeStyle)
        {
            var s = el.Style;
            sb.Append(",\"style\":{");
            Col(sb, "background", s.BackgroundColor); sb.Append(',');
            Col(sb, "color", s.TextColor); sb.Append(',');
            Num(sb, "fontSize", s.FontSize); sb.Append(',');
            Num(sb, "opacity", s.Opacity); sb.Append(',');
            Str(sb, "display", s.Display.ToString()); sb.Append(',');
            Str(sb, "position", s.Position.ToString()); sb.Append(',');
            Str(sb, "flexDirection", s.FlexDirection.ToString()); sb.Append(',');
            Num(sb, "borderRadius", s.BorderRadiusTopLeft); sb.Append(',');
            Num(sb, "borderWidth", s.BorderWidthVisual);
            sb.Append('}');
        }

        var kids = el.Children;
        if (kids.Count > 0)
        {
            sb.Append(",\"children\":[");
            for (int i = 0; i < kids.Count; i++)
            {
                if (i > 0) sb.Append(',');
                Write(sb, kids[i], includeStyle, depth + 1);
            }
            sb.Append(']');
        }
        sb.Append('}');
    }

    // Find the deepest element at a logical point (for the debugger's "pick" tool). Mirrors hit-test but
    // ignores PickingEnabled so you can inspect overlays too.
    public static VisualElement Pick(VisualElement root, Vector2 p)
    {
        if (root == null || !root.ResolvedRect.Contains(p)) return null;
        var kids = root.Children;
        for (int i = kids.Count - 1; i >= 0; i--)
        {
            var hit = Pick(kids[i], p);
            if (hit != null) return hit;
        }
        return root;
    }

    // A flat one-line summary per element (for `bal ui dump --flat` / quick logs).
    public static string ToTreeText(VisualElement el)
    {
        var sb = new StringBuilder();
        Tree(sb, el, 0);
        return sb.ToString();
    }

    static void Tree(StringBuilder sb, VisualElement el, int depth)
    {
        sb.Append(' ', depth * 2);
        sb.Append(el.TypeName);
        if (!string.IsNullOrEmpty(el.Name)) sb.Append('#').Append(el.Name);
        foreach (var c in el.ClassList) sb.Append('.').Append(c);
        var r = el.ResolvedRect;
        sb.Append($"  [{r.X:0},{r.Y:0} {r.Width:0}x{r.Height:0}]\n");
        foreach (var k in el.Children) Tree(sb, k, depth + 1);
    }

    // --- tiny JSON writers (no dependency) ---
    static void Str(StringBuilder sb, string k, string v) { sb.Append('"').Append(k).Append("\":"); Json(sb, v); }
    static void Num(StringBuilder sb, string k, float v) { sb.Append('"').Append(k).Append("\":").Append(v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)); }
    static void Col(StringBuilder sb, string k, Color c)
    {
        sb.Append('"').Append(k).Append("\":\"");
        sb.Append('#').Append(((int)(c.R * 255)).ToString("X2")).Append(((int)(c.G * 255)).ToString("X2"))
          .Append(((int)(c.B * 255)).ToString("X2")).Append(((int)(c.A * 255)).ToString("X2"));
        sb.Append('"');
    }
    static void Json(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char ch in s ?? "")
            switch (ch) { case '"': sb.Append("\\\""); break; case '\\': sb.Append("\\\\"); break; case '\n': sb.Append("\\n"); break; default: sb.Append(ch); break; }
        sb.Append('"');
    }
}
