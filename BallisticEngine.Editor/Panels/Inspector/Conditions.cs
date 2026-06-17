using System;
using System.Collections.Generic;

namespace BallisticEngine.Editor.Inspector;

// Shared evaluation of [ShowIf]/[HideIf]/[EnableIf]/[DisableIf]. The single source of truth both inspector
// paths run through (B0): the composable drawer stack's VisibilityStep/EnableStep call these for every value
// row (component AND volume), and the component LAYOUT driver (InspectorPanel.DrawMemberList) calls
// Conditions.Visible for its skip + foldout chrome — but it now shares this same evaluator with the stack,
// so the old component-vs-volume drift (each side reimplementing the condition logic) is gone. A sibling that
// resolves to a VolumeParameter is unwrapped to its .Value, so the same attribute works on plain components
// and volume overrides. A missing sibling fails OPEN (never hides / never disables) so a typo'd condition
// can't blank the inspector.
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
            return true;                                  // unknown sibling: fail open
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
