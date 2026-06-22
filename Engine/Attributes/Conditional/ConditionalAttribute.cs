namespace BallisticEngine;

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
