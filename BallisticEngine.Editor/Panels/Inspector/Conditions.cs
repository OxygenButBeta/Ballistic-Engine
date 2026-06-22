namespace BallisticEngine.Editor.Inspector;

public static class Conditions {
    public static bool Visible(IReadOnlyList<ConditionalAttribute> conditionals, object owner) {
        if (conditionals is null) return true;
        foreach (ConditionalAttribute c in conditionals) {
            if (c.Kind == ConditionKind.Show && !Eval(c, owner)) return false;
            if (c.Kind == ConditionKind.Hide && Eval(c, owner)) return false;
        }
        return true;
    }

    public static bool Disabled(IReadOnlyList<ConditionalAttribute> conditionals, object owner) {
        if (conditionals is null) return false;
        foreach (ConditionalAttribute c in conditionals) {
            if (c.Kind == ConditionKind.Enable && !Eval(c, owner)) return true;
            if (c.Kind == ConditionKind.Disable && Eval(c, owner)) return true;
        }
        return false;
    }

    static bool Eval(ConditionalAttribute c, object owner) {
        if (!InspectorReflection.TryGetSibling(owner, c.Member, out object v))
            return true;
        if (c.HasExpected)
            return Equals(v, c.Expected);
        return IsTruthy(v);
    }

    static bool IsTruthy(object v) => v switch {
        null => false,
        bool b => b,
        int i => i != 0,
        float f => f != 0f,
        Enum e => Convert.ToInt64(e) != 0,
        _ => true,
    };
}
