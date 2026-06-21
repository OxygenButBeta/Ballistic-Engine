using System.Collections.Generic;
using System.Text;

namespace BallisticEngine.UI;

// Serializes a set of style rules to .uss text — the stylesheet OUTPUT half of the visual UI Builder.
// A rule is a (selector, Style) pair; the Style is carried on an ordinary VisualElement (the Builder
// keeps one hidden "carrier" element per rule so the same Style.* setters + StyleSerialize the inspector
// and inline path use also produce the rule body — one serialization authority, no drift).
//
// Output is the standard USS the engine's StyleSheet.Parse + StyleApplier already read, so a written
// sheet round-trips: UssWriter.Write(rules) -> StyleSheet.Parse -> the cascade reproduces the same
// resolved styles. Each rule becomes `<selector> {\n  prop: val;\n  ...\n}`; rules with no non-default
// declaration are still emitted (an empty `{ }`) so a selector the author created but hasn't styled yet
// survives a save/reload (UITK keeps empty rules too).
public static class UssWriter
{
    public readonly struct Rule
    {
        public Rule(string selector, VisualElement carrier) { Selector = selector; Carrier = carrier; }
        public string Selector { get; }
        public VisualElement Carrier { get; }   // its Style holds the rule's declarations
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

    // Convenience for the common case: a plain dict of selector -> carrier (order not guaranteed, so the
    // list overload is preferred for stable diffs; this sorts by selector for determinism).
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
        // Reuse the SAME declaration diff the inline path uses; split on ';' into one indented line each.
        string decls = StyleSerialize.DiffFromDefaults(rule.Carrier.Style);
        if (!string.IsNullOrEmpty(decls))
            foreach (var d in decls.Split(';', System.StringSplitOptions.RemoveEmptyEntries))
                sb.Append("    ").Append(d.Trim()).Append(";\n");
        sb.Append("}\n");
    }
}
