namespace BallisticEngine;

// Optional metadata for a Behaviour. Components are discovered without it;
// the attribute only customizes how they appear in the editor's Add Component menu.
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ComponentAttribute : Attribute {
    public string DisplayName { get; }
    public string Menu { get; }

    // When true, the type is still REGISTERED (so existing scenes that reference it deserialize, and
    // the renderer can resolve it) but does NOT appear in the editor's Add-Component menu — for
    // components that are now automatic/internal and shouldn't be hand-placed (e.g. the auto-fit
    // IrradianceVolume / ReflectionVolume, which run by default and are tweaked via volume overrides).
    public bool HideFromAddMenu { get; set; }

    public ComponentAttribute(string displayName = null, string menu = null) {
        DisplayName = displayName;
        Menu = menu;
    }
}
