namespace BallisticEngine;

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class SpaceAttribute : Attribute {
    public float Height { get; }
    public SpaceAttribute(float height = 8f) => Height = height;
}
