using System;
using System.Collections.Generic;

namespace BallisticEngine.UI;

// How a compound selector relates to the one before it in the chain (CSS combinators).
public enum Combinator
{
    Descendant,   // "a b"  — b is any descendant of a
    Child,        // "a > b" — b is a direct child of a
    AdjacentSibling, // "a + b" — b immediately follows a among siblings
    GeneralSibling,  // "a ~ b" — b follows a (any later sibling)
}

// A single compound selector segment, e.g. "Button.primary#buy:hover" — a type, plus any number of
// .classes, an optional #name, and any number of pseudo-states. The cascade matches these against
// elements and orders rules by specificity (CSS rule: id > class/pseudo > type).
public sealed class SimpleSelector
{
    public string TypeName;                  // null = universal (matches any type)
    public readonly List<string> Classes = new();
    public string Name;                      // #name (maps to Element.Name)
    public readonly List<string> PseudoStates = new(); // hover/active/disabled/focus/checked — surfaced as state classes
    public readonly List<string> NotClasses = new();   // :not(.x) — negated classes (P2.6 minimal)
    public Combinator CombinatorToPrev = Combinator.Descendant; // relation to the previous compound in the chain

    // CSS specificity packed as (ids, classes+pseudos, types).
    public (int a, int b, int c) Specificity()
    {
        int a = Name != null ? 1 : 0;
        int b = Classes.Count + PseudoStates.Count + NotClasses.Count;
        int c = TypeName != null ? 1 : 0;
        return (a, b, c);
    }

    public bool Matches(VisualElement el)
    {
        if (TypeName != null && !TypeMatches(el, TypeName)) return false;
        if (Name != null && el.Name != Name) return false;

        foreach (var cls in Classes)
            if (!el.ClassListContains(cls)) return false;

        // Pseudo-states are surfaced as state-classes (hover/active/focus by the input module; disabled
        // by the element). A pseudo matches iff that state-class is present. Kept SEPARATE from `Classes`
        // so a real class named "hover" doesn't masquerade as the :hover state and vice-versa is explicit.
        foreach (var ps in PseudoStates)
            if (!el.ClassListContains(ps)) return false;

        foreach (var nc in NotClasses)
            if (el.ClassListContains(nc)) return false;

        return true;
    }

    // A type selector matches the element's type or any base type name up the chain, so a rule on
    // "Label" also styles a Button (Button : Label) — mirroring CSS matching a tag on subclasses.
    static bool TypeMatches(VisualElement el, string typeName)
    {
        for (var t = el.GetType(); t != null && t != typeof(object); t = t.BaseType)
            if (string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}

// A full selector is a chain of compound selectors joined by combinators. Right-to-left matching: the
// last compound must match the target, then each earlier compound must match a related element per its
// combinator (descendant/child/adjacent-sibling/general-sibling) — full CSS combinator support (P2.6).
public sealed class Selector
{
    public readonly List<SimpleSelector> Chain = new(); // ancestor-most first, target last

    public (int a, int b, int c) Specificity()
    {
        int a = 0, b = 0, c = 0;
        foreach (var s in Chain) { var (sa, sb, sc) = s.Specificity(); a += sa; b += sb; c += sc; }
        return (a, b, c);
    }

    public bool Matches(VisualElement el)
    {
        if (Chain.Count == 0) return false;
        return MatchFrom(Chain.Count - 1, el);
    }

    // Recursive right-to-left matcher honoring each compound's combinator-to-previous.
    bool MatchFrom(int i, VisualElement el)
    {
        if (el == null) return false;
        if (!Chain[i].Matches(el)) return false;
        if (i == 0) return true;

        var prev = Chain[i];   // its CombinatorToPrev says how compound i-1 relates to el
        switch (prev.CombinatorToPrev)
        {
            case Combinator.Child:
                return MatchFrom(i - 1, el.Parent);

            case Combinator.AdjacentSibling:
            {
                var sib = PrevSibling(el);
                return sib != null && MatchFrom(i - 1, sib);
            }

            case Combinator.GeneralSibling:
            {
                for (var sib = PrevSibling(el); sib != null; sib = PrevSibling(sib))
                    if (MatchFrom(i - 1, sib)) return true;
                return false;
            }

            default: // Descendant: try every ancestor
                for (var a = el.Parent; a != null; a = a.Parent)
                    if (MatchFrom(i - 1, a)) return true;
                return false;
        }
    }

    static VisualElement PrevSibling(VisualElement el)
    {
        var p = el.Parent;
        if (p == null) return null;
        var kids = p.Children;
        int idx = -1;
        for (int k = 0; k < kids.Count; k++) if (kids[k] == el) { idx = k; break; }
        return idx > 0 ? kids[idx - 1] : null;
    }
}
