namespace BallisticEngine;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class ComponentPreviewAttribute : Attribute {
    public Type TargetType { get; }
    public int Priority { get; }
    public ComponentPreviewAttribute(Type targetType, int priority = 0) {
        TargetType = targetType;
        Priority = priority;
    }
}
