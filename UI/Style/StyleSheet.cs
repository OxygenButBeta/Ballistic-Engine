using System.Text;

namespace BallisticEngine.UI;

public sealed class StyleSheet
{
    public sealed class Rule
    {
        public Selector Selector;
        public string Declarations;
        public int Order;
    }

    readonly List<Rule> _rules = new();
    public IReadOnlyList<Rule> Rules => _rules;

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

            sheet.CollectVars(body);

            foreach (var sel in selectorPart.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var parsed = ParseSelector(sel.Trim());
                if (parsed == null) continue;
                sheet._rules.Add(new Rule { Selector = parsed, Declarations = body, Order = order++ });
            }
        }

        return sheet;
    }

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

    static Selector ParseSelector(string text)
    {
        if (text.Length == 0 || text[0] == '@') return null;

        var selector = new Selector();
        var tokens = text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        Combinator pending = Combinator.Descendant;
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

    static SimpleSelector ParseCompound(string text)
    {
        var s = new SimpleSelector();

        int notIdx;
        while ((notIdx = text.IndexOf(":not(", StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int close = text.IndexOf(')', notIdx + 5);
            if (close < 0) break;
            string inner = text[(notIdx + 5)..close].Trim();
            if (inner.StartsWith(".")) s.NotClasses.Add(inner[1..]);
            text = text[..notIdx] + text[(close + 1)..];
        }

        int idx = 0;
        var token = new StringBuilder();
        char kind = ' ';

        void Flush()
        {
            if (token.Length == 0) return;
            string t = token.ToString();
            switch (kind)
            {
                case '.': s.Classes.Add(t); break;
                case '#': s.Name = t; break;
                case ':': s.PseudoStates.Add(MapPseudo(t)); break;
                default: if (t != "*") s.TypeName = t; break;
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
