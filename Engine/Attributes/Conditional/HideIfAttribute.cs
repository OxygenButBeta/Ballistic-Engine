namespace BallisticEngine;

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = true, Inherited = false)]
public sealed class HideIfAttribute : ConditionalAttribute {
    public HideIfAttribute(string member) : base(ConditionKind.Hide, member, null, false) { }
    public HideIfAttribute(string member, object expected) : base(ConditionKind.Hide, member, expected, true) { }
}
