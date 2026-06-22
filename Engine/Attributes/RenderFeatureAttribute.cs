namespace BallisticEngine;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RenderFeatureAttribute : Attribute {
    public string DisplayName { get; }
    public string Menu { get; }

    public bool HideFromAddMenu { get; set; }

    public RenderFeatureAttribute(string displayName = null, string menu = null) {
        DisplayName = displayName;
        Menu = menu;
    }
}
