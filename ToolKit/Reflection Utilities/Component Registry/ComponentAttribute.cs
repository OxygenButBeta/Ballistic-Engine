namespace BallisticEngine;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ComponentAttribute : Attribute {
    public string DisplayName { get; }
    public string Menu { get; }

    public bool HideFromAddMenu { get; set; }

    public ComponentAttribute(string displayName = null, string menu = null) {
        DisplayName = displayName;
        Menu = menu;
    }
}
