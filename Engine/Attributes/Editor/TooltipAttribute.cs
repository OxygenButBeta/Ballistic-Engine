namespace BallisticEngine;

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class TooltipAttribute : Attribute {
    public string Text { get; }
    public TooltipAttribute(string text) => Text = text;
}
