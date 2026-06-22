namespace BallisticEngine.UI;

public enum Combinator
{
    Descendant,
    Child,
    AdjacentSibling,
    GeneralSibling,
}

public sealed class SimpleSelector
{
    public string TypeName;
    public readonly List<string> Classes = new();
    public string Name;
    public readonly List<string> PseudoStates = new();
    public readonly List<string> NotClasses = new();
    public Combinator CombinatorToPrev = Combinator.Descendant;

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

        foreach (var ps in PseudoStates)
            if (!el.ClassListContains(ps)) return false;

        foreach (var nc in NotClasses)
            if (el.ClassListContains(nc)) return false;

        return true;
    }

    static bool TypeMatches(VisualElement el, string typeName)
    {
        for (var t = el.GetType(); t != null && t != typeof(object); t = t.BaseType)
            if (string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}

public sealed class Selector
{
    public readonly List<SimpleSelector> Chain = new();

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

    bool MatchFrom(int i, VisualElement el)
    {
        if (el == null) return false;
        if (!Chain[i].Matches(el)) return false;
        if (i == 0) return true;

        var prev = Chain[i];
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

            default:
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
