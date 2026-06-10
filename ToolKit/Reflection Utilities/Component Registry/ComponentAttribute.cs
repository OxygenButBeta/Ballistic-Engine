namespace BallisticEngine;

// Optional metadata for a Behaviour. Components are discovered without it;
// the attribute only customizes how they appear in the editor's Add Component menu.
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ComponentAttribute : Attribute {
    public string DisplayName { get; }
    public string Menu { get; }

    public ComponentAttribute(string displayName = null, string menu = null) {
        DisplayName = displayName;
        Menu = menu;
    }
}
