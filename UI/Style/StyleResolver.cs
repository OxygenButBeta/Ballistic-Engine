using System.Collections.Generic;

namespace BallisticEngine.UI;

// The resolved-style pipeline (P2 keystone). Replaces the old write-only additive cascade with a
// FROM-SCRATCH resolve per element:
//
//   1. Style.ResetToDefaults()         — revert every property to its CSS default
//   2. Style.InheritFrom(parentStyle)  — take inherited props (color/font-*) from the resolved parent
//   3. matched USS rules (specificity) — applied low→high across ALL sheets
//   4. inline style="" declarations    — highest precedence
//
// Because every resolve starts from defaults, removing a class or clearing a :hover/:active/:focus state
// and re-resolving REVERTS correctly — the additive-cascade "styles never come back" bug (3 critical
// findings) is gone by construction. Resolving top-down means a parent is resolved before its children,
// so inheritance reads the parent's final value.
public static class StyleResolver
{
    // Resolve the whole subtree rooted at `el`. `parentStyle` is the resolved style of el's parent (null
    // for the document root). Sheets are applied in order; later sheets win ties (source order across
    // sheets is preserved by passing them in order to CollectMatched).
    public static void ResolveTree(VisualElement el, IReadOnlyList<StyleSheet> sheets, Style parentStyle)
    {
        ResolveElement(el, sheets, parentStyle);
        var children = el.Children;
        for (int i = 0; i < children.Count; i++)
            ResolveTree(children[i], sheets, el.Style);
    }

    // Resolve a single element from scratch (no recursion). Used by per-element restyle (hover/class
    // change) where only the element + its inheriting subtree need re-resolving.
    public static void ResolveElement(VisualElement el, IReadOnlyList<StyleSheet> sheets, Style parentStyle)
    {
        // Capture imperative (code/control) overrides ONCE before any cascade runs, so a control's ctor
        // styles + game-code Style.* survive the from-scratch resolve as the highest-precedence layer
        // (Unity element.style parity). Replayed at the very end below.
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
                // Re-sort the merged set so cross-sheet ties resolve by (specificity, then global order).
                if (matched.Count > 1)
                    matched.Sort(static (x, y) =>
                    {
                        int cmp = CompareSpec(x.Selector.Specificity(), y.Selector.Specificity());
                        return cmp != 0 ? cmp : x.Order.CompareTo(y.Order);
                    });
                var vars = BuildVarStore(sheets);
                // Pass 1: normal declarations (low→high specificity).
                for (int i = 0; i < matched.Count; i++)
                    StyleApplier.ApplyInline(el.Style, matched[i].Declarations, StyleApplier.Pass.Normal, vars);
                // Inline normal beats matched normal.
                if (!string.IsNullOrEmpty(el.InlineStyle))
                    StyleApplier.ApplyInline(el.Style, el.InlineStyle, StyleApplier.Pass.Normal, vars);
                // Pass 2: !important declarations (same order) — every important beats every normal (P2.7).
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
            // No sheets — UXML inline only.
            StyleApplier.ApplyInline(el.Style, el.InlineStyle, StyleApplier.Pass.All, null);
        }

        // Imperative (code/control) overrides are the HIGHEST layer — re-applied last so they beat the
        // cascade + inline, matching Unity's element.style precedence.
        if (!string.IsNullOrEmpty(imperative))
            StyleApplier.ApplyInline(el.Style, imperative, StyleApplier.Pass.All, null);
    }

    // Merge custom properties across all sheets (later sheets win) into one var resolver. Cheap: built
    // per element; for hot paths the document can cache it, but correctness first.
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
