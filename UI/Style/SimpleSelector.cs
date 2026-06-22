namespace BallisticEngine.UI;

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
