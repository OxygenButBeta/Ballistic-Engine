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

// Excludes the member from BOTH scene serialization and the inspector: runtime-only state exposed
// as a public read/write property (e.g. Rigidbody.Velocity) that must never be authored into
// .scene files. The opposite trade-off from [HideInInspector], which keeps serialization.
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class NotSerializedAttribute : Attribute { }

// Unity parity: opts a NON-PUBLIC field (private / protected / internal) into scene serialization AND
// the inspector, so encapsulated state can be authored without a public field. By default only PUBLIC
// fields/properties are serializable state (ComponentReflection); a private field is invisible. Marking
// it [SerializeField] includes it — exactly like Unity's [SerializeField]. A PUBLIC member doesn't need
// it (it's already serialized); applying it there is harmless. Pair with [HideInInspector] to serialize a
// private field without showing it, or [NotSerialized] which wins (excludes from both even if marked).
[AttributeUsage(AttributeTargets.Field, Inherited = false)]
public sealed class SerializeFieldAttribute : Attribute { }

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

// Marks a PARAMETERLESS method as an "open editor window" trigger: the inspector shows a window-style
// button for it, and clicking opens a dedicated EditorWindow showing this component in a large, focused
// view (Unity's custom EditorWindow entry point, reduced to "give me a big window for this component").
// Use it for components whose authoring is awkward in the narrow inspector column (curves, graphs,
// large tables). Title defaults to "<Component>". The method itself still runs on click (so it can set
// up state), then the window opens.
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class EditorWindowExecutionPointAttribute : Attribute {
    public string Title { get; }
    public EditorWindowExecutionPointAttribute(string title = null) => Title = title;
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

// Marks a static, PARAMETERLESS method as a menu-bar command (Unity's [MenuItem]). The editor
// discovers every such method by reflection at bootstrap (TypeCache.GetMethodsWithAttribute<MenuItem>),
// builds the top menu bar from the slash-separated Path, and invokes the method when the entry is
// clicked. This is the self-registration primitive for the editor's window/command registry (editor
// rework Rule 3): a window opens itself by carrying a [MenuItem("Window/Xxx")] method that calls into
// the editor's window facade — EditorApplication never lists a window by name.
//
//   [MenuItem("Window/Inspector")] static void OpenInspector() => EditorWindows.Open("Inspector");
//
// Path = "TopMenu/Sub/.../Leaf"; the last segment is the clickable label, the rest are sub-menus.
// Order sorts siblings sharing the same parent path (ascending; ties break on the leaf label, then on
// the declaring method's full name — a stable total order independent of assembly-load order, so the
// menu is deterministic across machines/builds). The attribute lives in the engine assembly (zero
// ImGui/editor refs) so editor windows in the host assembly can carry it and the headless TypeCache
// scan in EngineBootstrap discovers them. AllowMultiple so one method can sit under several paths.
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class MenuItemAttribute : Attribute {
    public string Path { get; }
    public int Order { get; }
    public MenuItemAttribute(string path, int order = 0) {
        Path = path;
        Order = order;
    }
}

// Marks an editor IComponentPreview class as the custom inspector section for a component TYPE
// (editor-rework Rule 1 / Phase B1). REPLACES the hand-written `if (behaviour is Renderer/Volume/
// Terrain/...) DrawXxxSection(...)` god-chain in InspectorPanel: a preview self-registers by the
// component type it draws, and the inspector resolves the applicable previews from a registry by type
// — never an instanceof switch. Mirrors [MenuItem] (A1) / ComponentRegistry discovery exactly: the
// attribute lives in the engine (zero ImGui/editor refs) so the host-assembly preview classes carry it
// and the engine-side TypeCache scan discovers them headlessly; the editor's ComponentPreviewRegistry
// is the only thing that interprets it. TargetType is the component base/interface the preview applies
// to (assignable match, so a base-type preview covers subclasses). Priority breaks resolution order:
// higher draws first; ties break on the preview type's full name (DeterministicResolver) so the order
// is machine-independent. AllowMultiple = one preview can cover several component types.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class ComponentPreviewAttribute : Attribute {
    public Type TargetType { get; }
    public int Priority { get; }
    public ComponentPreviewAttribute(Type targetType, int priority = 0) {
        TargetType = targetType;
        Priority = priority;
    }
}

// Marks an editor IAssetInspector class as the custom inspector body for an asset FILE EXTENSION
// (editor-rework Rule 1 / Phase B2). REPLACES the `switch (ext) { case ".mat": DrawMaterialEditor(...);
// case ".png" or ...: DrawTextureImportSettings(...); ... }` god-switch in InspectorPanel.DrawAssetInspector
// — the asset-side mirror of B1's `if (behaviour is Renderer/Volume/...)` chain that Rule 1 deletes.
//
// Asset selection in this editor is PATH+EXTENSION+GUID-based (there is no single loaded "asset object"
// to switch on — `.scene`/`.shader`/`.glsl` aren't even backed by an instance), so B2 keys on the file
// EXTENSION rather than a Type — the exact analog of B1's TargetType. An inspector self-registers by the
// extension(s) it draws; the panel resolves the applicable inspector from a registry by extension, never a
// switch. Adding a custom asset body = adding one [AssetInspector] class; InspectorPanel is never edited.
//
// Mirrors [ComponentPreview] (B1) / [MenuItem] (A1) limb-for-limb: the attribute lives in the engine (zero
// ImGui/editor refs) so the host-assembly inspector classes carry it and the engine-side TypeCache scan
// discovers them headlessly; the editor's AssetInspectorRegistry is the only thing that interprets it.
// Extension is normalised to lower-case WITH the leading dot (".mat") on construction so a query matches the
// `Path.GetExtension(path).ToLowerInvariant()` the panel produces. AllowMultiple = one inspector class can
// cover several extensions (e.g. the texture body covers .png/.jpg/.tga/.hdr/...). Priority breaks resolution
// order when two inspectors claim the SAME extension: higher wins; ties break on the inspector type's full
// name (DeterministicResolver) so the winner is machine-independent.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class AssetInspectorAttribute : Attribute {
    public string Extension { get; }
    public int Priority { get; }
    public AssetInspectorAttribute(string extension, int priority = 0) {
        // Normalise to ".ext" lower-case so it matches Path.GetExtension(...).ToLowerInvariant().
        extension = (extension ?? string.Empty).Trim().ToLowerInvariant();
        if (extension.Length > 0 && extension[0] != '.')
            extension = "." + extension;
        Extension = extension;
        Priority = priority;
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
