namespace BallisticEngine.UI;

public static class StyleResolver
{
    public static void ResolveTree(VisualElement el, IReadOnlyList<StyleSheet> sheets, Style parentStyle)
    {
        ResolveElement(el, sheets, parentStyle);
        var children = el.Children;
        for (int i = 0; i < children.Count; i++)
            ResolveTree(children[i], sheets, el.Style);
    }

    public static void ResolveElement(VisualElement el, IReadOnlyList<StyleSheet> sheets, Style parentStyle)
    {
        string imperative = el.Style.CaptureImperativeOverrides();

        el.Style.ResetToDefaults();
        el.Style.InheritFrom(parentStyle);

        if (sheets != null && sheets.Count > 0)
        {
            List<StyleSheet.Rule> matched = null;
            for (int s = 0; s < sheets.Count; s++)
                matched = sheets[s].CollectMatched(el, matched);

            if (matched != null)
            {
                if (matched.Count > 1)
                    matched.Sort(static (x, y) =>
                    {
                        int cmp = CompareSpec(x.Selector.Specificity(), y.Selector.Specificity());
                        return cmp != 0 ? cmp : x.Order.CompareTo(y.Order);
                    });
                var vars = BuildVarStore(sheets);
                for (int i = 0; i < matched.Count; i++)
                    StyleApplier.ApplyInline(el.Style, matched[i].Declarations, StyleApplier.Pass.Normal, vars);
                if (!string.IsNullOrEmpty(el.InlineStyle))
                    StyleApplier.ApplyInline(el.Style, el.InlineStyle, StyleApplier.Pass.Normal, vars);
                for (int i = 0; i < matched.Count; i++)
                    StyleApplier.ApplyInline(el.Style, matched[i].Declarations, StyleApplier.Pass.Important, vars);
                if (!string.IsNullOrEmpty(el.InlineStyle))
                    StyleApplier.ApplyInline(el.Style, el.InlineStyle, StyleApplier.Pass.Important, vars);
            }
            else if (!string.IsNullOrEmpty(el.InlineStyle))
            {
                StyleApplier.ApplyInline(el.Style, el.InlineStyle, StyleApplier.Pass.All, BuildVarStore(sheets));
            }
        }
        else if (!string.IsNullOrEmpty(el.InlineStyle))
        {
            StyleApplier.ApplyInline(el.Style, el.InlineStyle, StyleApplier.Pass.All, null);
        }

        if (!string.IsNullOrEmpty(imperative))
            StyleApplier.ApplyInline(el.Style, imperative, StyleApplier.Pass.All, null);
    }

    static IVarResolver BuildVarStore(IReadOnlyList<StyleSheet> sheets)
    {
        if (sheets == null || sheets.Count == 0) return null;
        var store = new VarStore();
        for (int s = 0; s < sheets.Count; s++)
            foreach (var kv in sheets[s].Variables)
                store.Vars[kv.Key] = kv.Value;
        return store.Vars.Count > 0 ? store : null;
    }

    sealed class VarStore : IVarResolver
    {
        public readonly Dictionary<string, string> Vars = new();
        public string ResolveVar(string name) => Vars.TryGetValue(name, out var v) ? v : null;
    }

    static int CompareSpec((int a, int b, int c) x, (int a, int b, int c) y)
    {
        if (x.a != y.a) return x.a.CompareTo(y.a);
        if (x.b != y.b) return x.b.CompareTo(y.b);
        return x.c.CompareTo(y.c);
    }
}
