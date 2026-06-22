namespace BallisticEngine;

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class PropertyOrderAttribute : Attribute {
    public int Order { get; }
    public PropertyOrderAttribute(int order) => Order = order;
}
