using System.Xml;

namespace BallisticEngine.UI;

public static class UxmlLoader
{
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
                    el.InlineStyle = attr.Value;
                    StyleApplier.ApplyInline(el.Style, attr.Value);
                    break;

                case "picking-mode":
                    el.PickingEnabled = !attr.Value.Equals("Ignore", StringComparison.OrdinalIgnoreCase);
                    break;

                case "src":
                case "source":
                    if (el is Image img) img.Texture = attr.Value;
                    break;

                default:
                    break;
            }
        }
    }
}
