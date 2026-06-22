using System.Text;

namespace BallisticEngine.UI;

public static class UxmlWriter
{
    public static string Write(VisualElement root)
    {
        var sb = new StringBuilder();
        WriteElement(sb, root, 0);
        return sb.ToString();
    }

    static void WriteElement(StringBuilder sb, VisualElement el, int depth)
    {
        string pad = new(' ', depth * 2);
        string tag = el.TypeName;

        sb.Append(pad).Append('<').Append(tag);

        if (!string.IsNullOrEmpty(el.Name))
            Attr(sb, "name", el.Name);

        if (el.ClassList.Count > 0)
            Attr(sb, "class", string.Join(' ', el.ClassList));

        string text = TextOf(el);
        if (!string.IsNullOrEmpty(text))
            Attr(sb, "text", text);

        if (el is Image img && img.Texture is string src && !string.IsNullOrEmpty(src))
            Attr(sb, "src", src);

        string inline = el.InlineStyle ?? StyleSerialize.DiffFromDefaults(el.Style);
        if (!string.IsNullOrEmpty(inline))
            Attr(sb, "style", inline);

        if (!el.PickingEnabled)
            Attr(sb, "picking-mode", "Ignore");

        if (el.ChildCount == 0)
        {
            sb.Append(" />\n");
            return;
        }

        sb.Append(">\n");
        var children = el.Children;
        for (int i = 0; i < children.Count; i++)
            WriteElement(sb, children[i], depth + 1);
        sb.Append(pad).Append("</").Append(tag).Append(">\n");
    }

    static string TextOf(VisualElement el) => el switch
    {
        Button b => b.Text,
        Label l => l.Text,
        _ => null,
    };

    static void Attr(StringBuilder sb, string name, string value) =>
        sb.Append(' ').Append(name).Append("=\"").Append(Escape(value)).Append('"');

    static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                _ => c.ToString(),
            });
        return sb.ToString();
    }
}
