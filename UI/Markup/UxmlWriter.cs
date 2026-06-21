using System.Collections.Generic;
using System.Text;

namespace BallisticEngine.UI;

// Serializes a VisualElement tree back to .uxml text — the structural OUTPUT half of the visual UI
// Builder (UxmlLoader is the input half). Round-trips: UxmlWriter.Write(UxmlLoader.LoadFromText(x))
// reproduces the same tree (same tags, names, classes, text, inline style) so a built document re-opens
// identically. Emits the same attribute vocabulary the loader understands (name/class/text/style/src),
// so nothing the writer produces is silently dropped on reload.
//
// Inline style is written as style="<StyleSerialize diff>", the SAME declaration block the resolver
// replays as the imperative-override layer — so an element's authored look survives a reload through the
// loader (which calls StyleApplier.ApplyInline) + the USS cascade. Class-based styling lives in the
// sibling .uss (see UssWriter); an element can carry both (classes + a few inline tweaks), exactly like
// Unity.
public static class UxmlWriter
{
    // Default 2-space indent, UTF-8, no XML declaration (UITK .uxml omits it too; the loader is lenient).
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

        // Text-bearing elements (Label/Button) write their text as an attribute (the loader accepts both
        // attribute and inner text; the attribute form is unambiguous and indent-safe).
        string text = TextOf(el);
        if (!string.IsNullOrEmpty(text))
            Attr(sb, "text", text);

        // Image source path (the loader resolves it through AssetDatabase at load).
        if (el is Image img && img.Texture is string src && !string.IsNullOrEmpty(src))
            Attr(sb, "src", src);

        // Inline style. AUTHORITATIVE SOURCE = el.InlineStyle when the caller tracks it (the Builder sets
        // it to ONLY the element's true inline overrides, so a class-resolved value is never frozen into
        // inline — fixes inline-shadows-class). When InlineStyle is null (programmatic trees that never set
        // it), fall back to the resolved-style diff so plain Write(tree) still round-trips. Empty-string
        // InlineStyle = "explicitly no inline overrides" → write nothing.
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

    // XML attribute escaping (the values can contain &, <, >, " — the loader's XmlDocument requires it).
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
