namespace BallisticEngine;

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class HeaderAttribute : Attribute {
    public string Text { get; }
    public HeaderAttribute(string text) => Text = text;
}
