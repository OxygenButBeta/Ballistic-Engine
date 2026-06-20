using System;
using System.Collections.Generic;
using System.Text;

namespace BallisticEngine.UI;

// A parsed .uss stylesheet: an ordered list of rules (selector + declaration block). Apply() runs the
// cascade over a VisualElement tree — for each element, every matching rule's declarations are applied
// in specificity order (low to high) so the most specific rule wins, then inline style="" (applied at
// load time, highest precedence) sits on top. This is the styling half of a ported design.
//
// Supported selectors (Selector.cs): type (Button), .class, #name, :pseudo (hover/active/disabled/
// focus), descendant combinator (space), and comma-separated selector lists. Comments (/* ... */) and
// :root-style blocks are tolerated. Unsupported at-rules are skipped, not fatal.
public sealed class StyleSheet
{
    public sealed class Rule
    {
        public Selector Selector;
        public string Declarations;          // raw "prop: val; ..." applied via StyleApplier
        public int Order;                    // source order, tiebreaker for equal specificity
    }

    readonly List<Rule> _rules = new();
    public IReadOnlyList<Rule> Rules => _rules;

    // Custom properties (var tokens) collected from any rule body during Parse — "--name" -> value.
    // CSS scopes these to the selector; we collect them globally (pragmatic, matches how design tokens
    // are authored under :root). The resolver merges these across sheets into one var store. (P2.4)
    readonly Dictionary<string, string> _vars = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> Variables => _vars;

    public static StyleSheet Parse(string uss)
    {
        var sheet = new StyleSheet();
        if (string.IsNullOrWhiteSpace(uss)) return sheet;

        string text = StripComments(uss);
        int order = 0;
        int i = 0;

        while (i < text.Length)
        {
            int braceOpen = text.IndexOf('{', i);
            if (braceOpen < 0) break;
            int braceClose = text.IndexOf('}', braceOpen + 1);
            if (braceClose < 0) break;

            string selectorPart = text[i..braceOpen].Trim();
            string body = text[(braceOpen + 1)..braceClose].Trim();
            i = braceClose + 1;

            if (selectorPart.Length == 0) continue;

            // Collect custom properties (--name: value) from this body into the global var store.
            sheet.CollectVars(body);

            // A selector list "a, b, c { ... }" produces one rule per selector sharing the body.
            foreach (var sel in selectorPart.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var parsed = ParseSelector(sel.Trim());
                if (parsed == null) continue; // unsupported (e.g. an at-rule) — skip quietly
                sheet._rules.Add(new Rule { Selector = parsed, Declarations = body, Order = order++ });
            }
        }

        return sheet;
    }

    // Runs the cascade over the whole subtree rooted at `root` (inclusive). Kept for callers that want a
    // sheet to apply its matched declarations on top of the current style (legacy additive path). The
    // resolved-style pipeline (StyleResolver) uses CollectMatched + a from-scratch resolve instead, which
    // is what makes :hover revert + inheritance correct.
    public void Apply(VisualElement root)
    {
        ApplyToElement(root);
        foreach (var d in root.Descendants())
            ApplyToElement(d);
    }

    void ApplyToElement(VisualElement el)
    {
        var matched = CollectMatched(el, null);
        if (matched == null) return;
        foreach (var rule in matched)
            StyleApplier.ApplyInline(el.Style, rule.Declarations);
    }

    // Collect this sheet's rules matching `el`, sorted ascending by specificity then source order, into
    // `into` (allocated if null). The resolver merges matched rules from multiple sheets before applying,
    // so it passes a shared list. Returns the list (or null if nothing matched and `into` was null).
    public List<Rule> CollectMatched(VisualElement el, List<Rule> into)
    {
        List<Rule> matched = into;
        foreach (var rule in _rules)
        {
            if (rule.Selector.Matches(el))
                (matched ??= new List<Rule>()).Add(rule);
        }
        if (matched != null && matched.Count > 1)
            matched.Sort(static (x, y) =>
            {
                int cmp = CompareSpecificity(x.Selector.Specificity(), y.Selector.Specificity());
                return cmp != 0 ? cmp : x.Order.CompareTo(y.Order);
            });
        return matched;
    }

    // Extract "--name: value" declarations from a rule body into _vars. Values keep var()/literal text;
    // the applier resolves nested var() at use time.
    void CollectVars(string body)
    {
        foreach (var decl in body.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = decl.IndexOf(':');
            if (colon <= 0) continue;
            string name = decl[..colon].Trim();
            if (!name.StartsWith("--")) continue;
            _vars[name] = decl[(colon + 1)..].Trim();
        }
    }

    static int CompareSpecificity((int a, int b, int c) x, (int a, int b, int c) y)
    {
        if (x.a != y.a) return x.a.CompareTo(y.a);
        if (x.b != y.b) return x.b.CompareTo(y.b);
        return x.c.CompareTo(y.c);
    }

    // ---------------------------------------------------------------- parsing helpers

    // Parses "Button.primary > .row + Label:hover" into a Selector with real combinators (P2.6).
    // Returns null for unsupported input (an at-rule like @media, or an empty token).
    static Selector ParseSelector(string text)
    {
        if (text.Length == 0 || text[0] == '@') return null;

        var selector = new Selector();
        var tokens = text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        Combinator pending = Combinator.Descendant;   // combinator that joins the NEXT compound to the previous
        bool first = true;

        foreach (var tok in tokens)
        {
            if (tok == ">") { pending = Combinator.Child; continue; }
            if (tok == "+") { pending = Combinator.AdjacentSibling; continue; }
            if (tok == "~") { pending = Combinator.GeneralSibling; continue; }

            var compound = ParseCompound(tok);
            if (compound == null) continue;
            compound.CombinatorToPrev = first ? Combinator.Descendant : pending;
            selector.Chain.Add(compound);
            pending = Combinator.Descendant;
            first = false;
        }
        return selector.Chain.Count > 0 ? selector : null;
    }

    // Parses one compound like "Button.primary#buy:hover" or ".chip:not(.disabled)" or "*".
    static SimpleSelector ParseCompound(string text)
    {
        var s = new SimpleSelector();

        // Pull out :not(...) groups first (they contain a nested simple selector fragment).
        int notIdx;
        while ((notIdx = text.IndexOf(":not(", StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int close = text.IndexOf(')', notIdx + 5);
            if (close < 0) break;
            string inner = text[(notIdx + 5)..close].Trim();
            // minimal: only .class negation is modeled
            if (inner.StartsWith(".")) s.NotClasses.Add(inner[1..]);
            text = text[..notIdx] + text[(close + 1)..];
        }

        int idx = 0;
        var token = new StringBuilder();
        char kind = ' '; // ' ' = type, '.' = class, '#' = name, ':' = pseudo

        void Flush()
        {
            if (token.Length == 0) return;
            string t = token.ToString();
            switch (kind)
            {
                case '.': s.Classes.Add(t); break;
                case '#': s.Name = t; break;
                case ':': s.PseudoStates.Add(MapPseudo(t)); break;
                default: if (t != "*") s.TypeName = t; break; // '*' = universal -> null type
            }
            token.Clear();
        }

        while (idx < text.Length)
        {
            char ch = text[idx];
            if (ch is '.' or '#' or ':')
            {
                Flush();
                kind = ch;
            }
            else
            {
                token.Append(ch);
            }
            idx++;
        }
        Flush();
        return s;
    }

    // Maps CSS pseudo-class names to the state-class names the input module/elements set. Unknown
    // pseudos pass through as-is (so a custom state class still matches).
    static string MapPseudo(string pseudo) => pseudo.ToLowerInvariant() switch
    {
        "hover" => "hover",
        "active" => "active",
        "disabled" => "disabled",
        "focus" => "focus",
        "checked" => "checked",
        _ => pseudo,
    };

    static string StripComments(string s)
    {
        // Remove /* ... */ blocks. Cheap single-pass; USS has no // line comments.
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*')
            {
                int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) break;
                i = end + 1;
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
