namespace BallisticEngine;

[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class ButtonAttribute : Attribute {
    public string Label { get; }
    public ButtonAttribute(string label = null) => Label = label;
}
