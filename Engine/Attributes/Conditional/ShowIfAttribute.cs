namespace BallisticEngine;

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = true, Inherited = false)]
public sealed class ShowIfAttribute : ConditionalAttribute {
    public ShowIfAttribute(string member) : base(ConditionKind.Show, member, null, false) { }
    public ShowIfAttribute(string member, object expected) : base(ConditionKind.Show, member, expected, true) { }
}
