namespace BallisticEngine;

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = true, Inherited = false)]
public sealed class EnableIfAttribute : ConditionalAttribute {
    public EnableIfAttribute(string member) : base(ConditionKind.Enable, member, null, false) { }
    public EnableIfAttribute(string member, object expected) : base(ConditionKind.Enable, member, expected, true) { }
}
