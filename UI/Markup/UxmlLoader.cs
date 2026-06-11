using System;
using System.Xml;

namespace BallisticEngine.UI;

// Parses a .uxml document (XML) into a VisualElement tree — the structural half of a ported design.
// Each XML element becomes a VisualElement via ElementFactory; attributes map to engine concepts:
//   name="..."          -> Element.Name (the Q<>() handle)
//   class="a b c"        -> AddToClassList for each token (USS hooks onto these)
//   text="..."           -> Label/Button text (also inner text content works: <Label>Hi</Label>)
//   style="..."          -> inline computed style via StyleApplier (highest precedence, like CSS)
//   picking-mode="Ignore"-> PickingEnabled = false (visual overlays)
// Unknown attributes are ignored (logged at debug level) so a richer source document degrades
// gracefully rather than failing the whole load.
//
// Resilience is deliberate (Unity/AssetDatabase parity): a malformed document logs and returns null
// rather than throwing into the caller — UI authoring must never crash the engine.
public static class UxmlLoader
{
    // Builds a tree from raw UXML text. Returns the root element, or null on parse failure.
    public static VisualElement LoadFromText(string uxml)
    {
        if (string.IsNullOrWhiteSpace(uxml))
        {
            Debugging.LogWarning("UXML: empty document.");
            return null;
        }

        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(uxml);
            if (doc.DocumentElement == null)
            {
                Debugging.LogWarning("UXML: document has no root element.");
                return null;
            }
            return BuildElement(doc.DocumentElement);
        }
        catch (XmlException e)
        {
            Debugging.LogError($"UXML: parse error — {e.Message}");
            return null;
        }
    }

    static VisualElement BuildElement(XmlElement xml)
    {
        var el = ElementFactory.Create(xml.LocalName);

        ApplyAttributes(el, xml);

        // Children: element nodes recurse; significant text becomes the element's text (for
        // Label/Button) when no explicit text="" attribute was given.
        foreach (XmlNode node in xml.ChildNodes)
        {
            switch (node.NodeType)
            {
                case XmlNodeType.Element:
                    el.Add(BuildElement((XmlElement)node));
                    break;

                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                    var text = node.Value?.Trim();
                    if (!string.IsNullOrEmpty(text) && el is Label label && string.IsNullOrEmpty(label.Text))
                        label.Text = text;
                    break;
            }
        }

        return el;
    }

    static void ApplyAttributes(VisualElement el, XmlElement xml)
    {
        foreach (XmlAttribute attr in xml.Attributes)
        {
            switch (attr.LocalName.ToLowerInvariant())
            {
                case "name":
                    el.Name = attr.Value;
                    break;

                case "class":
                    foreach (var cls in attr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        el.AddToClassList(cls);
                    break;

                case "text":
                    if (el is Label label) label.Text = attr.Value;
                    break;

                case "style":
                    // Apply now AND remember the raw block: the UIDocument re-applies it after the USS
                    // cascade so inline always wins (CSS precedence: inline > stylesheet).
                    el.InlineStyle = attr.Value;
                    StyleApplier.ApplyInline(el.Style, attr.Value);
                    break;

                case "picking-mode":
                    el.PickingEnabled = !attr.Value.Equals("Ignore", StringComparison.OrdinalIgnoreCase);
                    break;

                case "src":
                case "source":
                    // Image source path — stored as the path string; the asset is resolved by the
                    // UIDocument/renderer through AssetDatabase (UI/ takes no AssetPipeline dependency).
                    if (el is Image img) img.Texture = attr.Value;
                    break;

                default:
                    // Tolerate unknown attributes silently-ish — keeps richer source docs loadable.
                    break;
            }
        }
    }
}
