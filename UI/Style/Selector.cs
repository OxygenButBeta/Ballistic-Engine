using System;
using System.Collections.Generic;

namespace BallisticEngine.UI;

// A single compound selector segment, e.g. "Button.primary#buy:hover" — a type, plus any number of
// .classes, an optional #name, and optional pseudo-state. The cascade matches these against elements
// and orders rules by specificity (CSS rule: id > class/pseudo > type), so later/more-specific rules
// win — the same precedence a ported design relied on in the browser.
public sealed class SimpleSelector
{
    public string TypeName;                  // null = universal (matches any type)
    public readonly List<string> Classes = new();
    public string Name;                      // #name (maps to Element.Name)
    public string PseudoState;               // hover/active/disabled/focus — matched as a class

    // CSS specificity packed as (ids, classes+pseudos, types). Compared field-by-field; higher wins.
    public (int a, int b, int c) Specificity()
    {
        int a = Name != null ? 1 : 0;
        int b = Classes.Count + (PseudoState != null ? 1 : 0);
        int c = TypeName != null ? 1 : 0;
        return (a, b, c);
    }

    public bool Matches(VisualElement el)
    {
        if (TypeName != null && !TypeMatches(el, TypeName)) return false;
        if (Name != null && el.Name != Name) return false;

        foreach (var cls in Classes)
            if (!el.ClassListContains(cls)) return false;

        // Pseudo-states are surfaced as classes by the input module (hover/active) or the element
        // (disabled), so a pseudo matches iff that class is present — uniform and zero special-casing.
        if (PseudoState != null && !el.ClassListContains(PseudoState)) return false;

        return true;
    }

    // A type selector matches the element's TypeName or any base type name up the chain, so a rule on
    // "Label" also styles a Button (Button : Label) — mirroring CSS matching a tag on subclasses.
    static bool TypeMatches(VisualElement el, string typeName)
    {
        for (var t = el.GetType(); t != null && t != typeof(object); t = t.BaseType)
            if (string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}

// A full selector is a chain of compound selectors separated by descendant combinators
// ("a b c" = c inside b inside a). v1 supports the descendant combinator only (space) — the common
// case in Claude designs; child/sibling combinators (>, +, ~) can be added later.
public sealed class Selector
{
    public readonly List<SimpleSelector> Chain = new(); // ancestor-most first, target last

    public (int a, int b, int c) Specificity()
    {
        int a = 0, b = 0, c = 0;
        foreach (var s in Chain) { var (sa, sb, sc) = s.Specificity(); a += sa; b += sb; c += sc; }
        return (a, b, c);
    }

    // Matches if the LAST compound matches `el`, and each earlier compound matches some ancestor in
    // order (not necessarily adjacent — descendant combinator). Standard right-to-left CSS matching.
    public bool Matches(VisualElement el)
    {
        if (Chain.Count == 0) return false;
        int i = Chain.Count - 1;
        if (!Chain[i].Matches(el)) return false;
        i--;

        var ancestor = el.Parent;
        while (i >= 0 && ancestor != null)
        {
            if (Chain[i].Matches(ancestor)) i--;
            ancestor = ancestor.Parent;
        }
        return i < 0;
    }
}
