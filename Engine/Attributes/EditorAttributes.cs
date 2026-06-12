namespace BallisticEngine;

// Inspector-authoring attributes (Unity-style). These live in the engine assembly so components
// can decorate their members with them; the editor's InspectorPanel is the only thing that
// *interprets* them. Deliberately ZERO ImGui/GL references — plain System.Attribute with
// primitive args — so the engine source stays free of editor/renderer dependencies.

// Renders a numeric member as a slider clamped to [Min, Max]. Applies to float and int members;
// the inspector also clamps the value on assignment so out-of-range data can't slip through.
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class RangeAttribute : Attribute {
    public float Min { get; }
    public float Max { get; }
    public RangeAttribute(float min, float max) {
        Min = min;
        Max = max;
    }
}

// Draws a bold section label above the decorated member (groups related members visually).
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class HeaderAttribute : Attribute {
    public string Text { get; }
    public HeaderAttribute(string text) => Text = text;
}

// Adds a hover tooltip (and a small "(?)" marker on the label) for the decorated member.
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class TooltipAttribute : Attribute {
    public string Text { get; }
    public TooltipAttribute(string text) => Text = text;
}

// Inserts vertical spacing before the decorated member.
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class SpaceAttribute : Attribute {
    public float Height { get; }
    public SpaceAttribute(float height = 8f) => Height = height;
}

// Hides the decorated member from the inspector WITHOUT affecting serialization — the value is
// still saved/loaded. (ComponentReflection.InspectorMembers honours this; SerializableMembers,
// the serialization contract, deliberately does not.)
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class HideInInspectorAttribute : Attribute { }

// Shows the member in the inspector but disables editing (greyed out).
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class ReadOnlyAttribute : Attribute { }

// Marks a Vector3 member as a color so the inspector shows a color picker instead of drag floats.
// Hdr = true allows values > 1 (intensity), matching Unity's [ColorUsage].
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class ColorUsageAttribute : Attribute {
    public bool Hdr { get; }
    public ColorUsageAttribute(bool hdr = false) => Hdr = hdr;
}

// Parity marker only. The engine already serializes public mutable fields; this attribute exists so
// component code reads like Unity's. It has no behaviour of its own in v1.
[AttributeUsage(AttributeTargets.Field, Inherited = false)]
public sealed class SerializeFieldAttribute : Attribute { }

// Excludes the member from BOTH scene serialization and the inspector: runtime-only state exposed
// as a public read/write property (e.g. Rigidbody.Velocity) that must never be authored into
// .scene files. The opposite trade-off from [HideInInspector], which keeps serialization.
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class NotSerializedAttribute : Attribute { }

// Renders a full-width button in the inspector that invokes the decorated PARAMETERLESS method
// when clicked (bake triggers, one-shot actions). Clearer than a self-resetting bool checkbox.
// Label defaults to the method name.
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class ButtonAttribute : Attribute {
    public string Label { get; }
    public ButtonAttribute(string label = null) => Label = label;
}

// Adds the decorated PARAMETERLESS method to the component's "..." / right-click context menu in the
// inspector (Unity's [ContextMenu]). For one-shot actions you want tucked away rather than shown as a
// full-width [Button]. Label defaults to the method name.
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ContextMenuAttribute : Attribute {
    public string Label { get; }
    public ContextMenuAttribute(string label = null) => Label = label;
}

// Puts this member (and following members that share the same group name) inside a collapsible
// foldout in the inspector. A member with a different group name, or a [Header], starts a new
// section. Use it to categorize a component's properties (e.g. "Shadows", "Advanced").
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class FoldoutGroupAttribute : Attribute {
    public string Name { get; }
    public bool DefaultOpen { get; }
    public FoldoutGroupAttribute(string name, bool defaultOpen = true) {
        Name = name;
        DefaultOpen = defaultOpen;
    }
}

// Marks a DataAsset subclass as creatable from the editor's asset browser (Unity's
// [CreateAssetMenu]). The browser adds a "Create > {Menu} > {DisplayName}" entry that writes a new
// .asset with this type's default values, named {FileName}. Discovered by ComponentRegistry.
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class CreateDataAssetAttribute : Attribute {
    public string Menu { get; }
    public string FileName { get; }
    public string DisplayName { get; }
    public CreateDataAssetAttribute(string menu = "", string fileName = "New Data Asset",
        string displayName = null) {
        Menu = menu;
        FileName = fileName;
        DisplayName = displayName;
    }
}
