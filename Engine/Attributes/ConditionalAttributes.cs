namespace BallisticEngine;

public enum ConditionKind { Show, Hide, Enable, Disable }

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

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class LabelTextAttribute : Attribute {
    public string Text { get; }
    public LabelTextAttribute(string text) => Text = text;
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class PropertyOrderAttribute : Attribute {
    public int Order { get; }
    public PropertyOrderAttribute(int order) => Order = order;
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class SerializeReferenceAttribute : Attribute { }
