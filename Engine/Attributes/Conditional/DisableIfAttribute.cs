namespace BallisticEngine;

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = true, Inherited = false)]
public sealed class DisableIfAttribute : ConditionalAttribute {
    public DisableIfAttribute(string member) : base(ConditionKind.Disable, member, null, false) { }
    public DisableIfAttribute(string member, object expected) : base(ConditionKind.Disable, member, expected, true) { }
}
