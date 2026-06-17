namespace BallisticEngine;

// Conditional + ordering inspector attributes (Odin-style). Like the rest of the inspector-authoring
// attributes (EditorAttributes.cs) these live in the ENGINE assembly so components can decorate their
// members, carry ZERO ImGui/GL references, and are interpreted only by the editor's drawer pipeline
// (BallisticEngine.Editor.Inspector). A condition names a SIBLING member on the same component; the
// editor resolves it once (cached) and reads its live value each frame.

public enum ConditionKind { Show, Hide, Enable, Disable }

// Base for [ShowIf]/[HideIf]/[EnableIf]/[DisableIf]. Two forms:
//   [ShowIf("enabled")]                  -> truthiness of the sibling (bool true / non-zero / non-null)
//   [ShowIf("mode", GiMode.RayTraced)]   -> equality against the given value (enums, ints, bools, ...)
// Multiple are allowed and AND-combined (every Show/Enable condition must pass; any Hide/Disable that
// matches hides/disables). A VolumeComponent sibling that is a VolumeParameter is unwrapped to its
// .Value before comparison, so the same attribute works on plain components and volume overrides.
public abstract class ConditionalAttribute : Attribute {
    public string Member { get; }
    public object Expected { get; }
    public bool HasExpected { get; }
    public ConditionKind Kind { get; }

    protected ConditionalAttribute(ConditionKind kind, string member, object expected, bool hasExpected) {
        Kind = kind;
        Member = member;
        Expected = expected;
        HasExpected = hasExpected;
    }
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = true, Inherited = false)]
public sealed class ShowIfAttribute : ConditionalAttribute {
    public ShowIfAttribute(string member) : base(ConditionKind.Show, member, null, false) { }
    public ShowIfAttribute(string member, object expected) : base(ConditionKind.Show, member, expected, true) { }
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = true, Inherited = false)]
public sealed class HideIfAttribute : ConditionalAttribute {
    public HideIfAttribute(string member) : base(ConditionKind.Hide, member, null, false) { }
    public HideIfAttribute(string member, object expected) : base(ConditionKind.Hide, member, expected, true) { }
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = true, Inherited = false)]
public sealed class EnableIfAttribute : ConditionalAttribute {
    public EnableIfAttribute(string member) : base(ConditionKind.Enable, member, null, false) { }
    public EnableIfAttribute(string member, object expected) : base(ConditionKind.Enable, member, expected, true) { }
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = true, Inherited = false)]
public sealed class DisableIfAttribute : ConditionalAttribute {
    public DisableIfAttribute(string member) : base(ConditionKind.Disable, member, null, false) { }
    public DisableIfAttribute(string member, object expected) : base(ConditionKind.Disable, member, expected, true) { }
}

// Overrides the inspector label (otherwise the member name is prettified).
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class LabelTextAttribute : Attribute {
    public string Text { get; }
    public LabelTextAttribute(string text) => Text = text;
}

// Sorts a member in the inspector (ascending; default 0 = declaration order). Lets a derived/important
// member float above others without reordering declarations.
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class PropertyOrderAttribute : Attribute {
    public int Order { get; }
    public PropertyOrderAttribute(int order) => Order = order;
}

// Marks a member whose DECLARED type is abstract/interface (or any base) as polymorphically serialized
// by CONCRETE TYPE (Unity's [SerializeReference]): the live concrete type is recorded as a $type tag and
// instantiated on load, and the inspector offers a TypeCache dropdown of implementors (editor-rework
// Rule 1.75 / §3.45 gap 2 / Trap 3). MARKER ONLY in P0.2 — the property model uses it to classify the
// member as PropertyCategory.Polymorphic so the traversal contract is complete; the $type codec + dropdown
// wiring land in Phase G3. Without this marker an abstract/interface member is left Unsupported (it can't
// be `new`'d, so the model won't silently recurse a base it can't instantiate).
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class SerializeReferenceAttribute : Attribute { }
