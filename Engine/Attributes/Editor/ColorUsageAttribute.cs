namespace BallisticEngine;

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class ColorUsageAttribute : Attribute {
    public bool Hdr { get; }
    public ColorUsageAttribute(bool hdr = false) => Hdr = hdr;
}
