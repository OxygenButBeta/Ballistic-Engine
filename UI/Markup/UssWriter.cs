using System.Text;

namespace BallisticEngine.UI;

public static class UssWriter
{
    public readonly struct Rule
    {
        public Rule(string selector, VisualElement carrier) { Selector = selector; Carrier = carrier; }
        public string Selector { get; }
        public VisualElement Carrier { get; }
    }

    public static string Write(IReadOnlyList<Rule> rules)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < rules.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            WriteRule(sb, rules[i]);
        }
        return sb.ToString();
    }

    public static string Write(IReadOnlyDictionary<string, VisualElement> rules)
    {
        var list = new List<Rule>(rules.Count);
        foreach (var kv in rules) list.Add(new Rule(kv.Key, kv.Value));
        list.Sort(static (a, b) => string.CompareOrdinal(a.Selector, b.Selector));
        return Write(list);
    }

    static void WriteRule(StringBuilder sb, Rule rule)
    {
        sb.Append(rule.Selector).Append(" {\n");
        string decls = StyleSerialize.DiffFromDefaults(rule.Carrier.Style);
        if (!string.IsNullOrEmpty(decls))
            foreach (var d in decls.Split(';', System.StringSplitOptions.RemoveEmptyEntries))
                sb.Append("    ").Append(d.Trim()).Append(";\n");
        sb.Append("}\n");
    }
}
