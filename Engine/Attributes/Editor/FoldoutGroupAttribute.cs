namespace BallisticEngine;

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class FoldoutGroupAttribute : Attribute {
    public string Name { get; }
    public bool DefaultOpen { get; }
    public FoldoutGroupAttribute(string name, bool defaultOpen = true) {
        Name = name;
        DefaultOpen = defaultOpen;
    }
}
