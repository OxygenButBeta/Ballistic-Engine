namespace BallisticEngine;

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class LabelTextAttribute : Attribute {
    public string Text { get; }
    public LabelTextAttribute(string text) => Text = text;
}
