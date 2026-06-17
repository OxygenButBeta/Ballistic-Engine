namespace BallisticEngine;

// How a value TYPE participates in the one recursive value-traversal (editor-rework P0.2, Rule 2).
// The SAME classification drives both ends of the pipeline: the serializer decides how to emit/parse a
// value, and the drawer tree decides whether a member is a leaf widget or a foldout that recurses. There
// is NO type-switch anywhere downstream — callers branch on this enum, which is computed ONCE per type in
// the compiled TypePlan and cached.
//
// Phase scope: Primitive/Enum/MathStruct/AssetRef/Nested/Unsupported are classifiable TODAY (the value
// shapes the current SceneSerializer already round-trips, plus the nested-struct case it currently drops).
// SceneObjectRef/Polymorphic/Collection are the Phase-G gaps (§3.45) — the model RECOGNISES them now so
// the traversal contract is complete from day 1, but the serializer/drawer wiring for them lands in G.
public enum PropertyCategory {
    // A leaf the codec writes as a scalar and a single widget edits: bool, the integral/floating numeric
    // types, char, string, decimal, DateTime, Guid. Bottoms out the recursion.
    Primitive,

    // A leaf written as its enum name; a dropdown edits it. Separated from Primitive because the drawer
    // and the [Flags] handling differ.
    Enum,

    // An OpenTK math value treated as a single leaf even though it has sub-fields: Vector2/3/4, Quaternion,
    // Matrix*, plus Color-tagged Vector3. The codec has dedicated converters and the editor a dedicated
    // multi-component widget, so the traversal does NOT recurse into x/y/z (that would defeat the widget).
    MathStruct,

    // A reference to a BObject that lives as a FILE ASSET (Material/Texture/Mesh/Shader/VolumeProfile/...):
    // serialized as `guid:<hex>`, edited via the asset picker slot. A leaf from the traversal's view (it
    // does not recurse into the referenced asset's members — that's the [InlineEditor] opt-in, later).
    AssetRef,

    // A reference to a runtime SCENE object (Entity / Component / Behaviour): a BObject WITHOUT an asset
    // GUID, identified by InstanceId. COMPLETELY ABSENT in the serializer today (§3.45 gap 1) — silently
    // dropped to null. The model recognises it (so G1 can wire the EntityRef resolver + scene picker); a
    // leaf from the traversal's view.
    SceneObjectRef,

    // A member whose DECLARED type is abstract/interface and is marked for [SerializeReference]: the live
    // CONCRETE type is known only per-instance, picked from a TypeCache dropdown, then its fields recurse.
    // ABSENT today (§3.45 gap 2). This is the one category whose child shape depends on the runtime value,
    // not the declared type — the DYNAMIC side of the cache boundary (§4): the child plan is keyed by the
    // ACTUAL type and the tree node rebuilds when that actual type changes.
    Polymorphic,

    // A List<T> / array / dictionary: element count is per-instance (the other DYNAMIC case — the node
    // rebuilds when the count changes); each element recurses through the same traversal. ABSENT today
    // (§3.45 gap 3) — a List<T> round-trips to null.
    Collection,

    // A plain struct/class member (the user's `struct Pair { int x, y; }` example, Rule 2): recurse the
    // SAME traversal into its serializable members, drawn as a foldout. Pass-through dumps it to YAML but
    // deserialize returns null today (§3.45 gap 4) — the model makes the recursion first-class.
    Nested,

    // No codec and no recursion applies (e.g. a delegate, an open-generic field, IntPtr). The serializer
    // must make this LOUD not silent (G0); the drawer shows an "unsupported" row. Classifying it as a
    // distinct category is what lets G0 flag the drop instead of nulling it quietly.
    Unsupported,
}
