namespace BallisticEngine;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ContextMenuAttribute : Attribute {
    public string Label { get; }
    public ContextMenuAttribute(string label = null) => Label = label;
}
