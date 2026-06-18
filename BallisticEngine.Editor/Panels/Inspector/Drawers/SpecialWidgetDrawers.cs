using System;
using System.Collections.Generic;

namespace BallisticEngine.Editor.Inspector;

// BEvent / AnimationCurve / ColorGradient / BObject all live in the root `BallisticEngine` namespace; this
// namespace (BallisticEngine.Editor.Inspector) is nested under it, so C#'s outer-namespace lookup resolves
// them with no using (matching InspectorPanel, which also references them unqualified). BEventEditor and
// EditorWidgets live in BallisticEngine.Editor (the parent of this namespace) -> also resolved by lookup.

// editor-rework B4 (Rule 2 — "serialize-a-value == draw-a-value, ONE recursion"). The four widget types that
// used to BYPASS the drawer stack via InspectorPanel.IsSpecialWidgetType (an if/else chain calling
// BEventEditor.Draw / DrawAssetSlot / EditorWidgets.CurveEditor / EditorWidgets.GradientEditor directly) are
// now TERMINAL drawers in the same B0 stack as every primitive. They register onto the component-path
// DrawerRegistry (InspectorPanel.memberRegistry) so the stack's TypeDrawerTerminalStep resolves them like any
// other leaf -- the IsSpecialWidgetType chain dissolves. The headless harness builds CreatePrimitive() (which
// has none of these), so the engine-only test never references editor/ImGui types.
//
// Byte-identical via DELEGATION (the B1-B4 invariant): each drawer makes the EXACT same widget call the old
// DrawMember arm made, with the same ImGui id (p.Name, under the stack's already-pushed PushId(p.Name)) and
// the same dirty-marking. Only the COMPOSITION changes (stack terminal instead of a hardcoded branch); the
// Enable/[ReadOnly] disable wrap + the row label/mixed-marker now come from the shared stack (EnableStep +
// ImGuiComponentGui.BeginRow), which the resolver proved equal to DrawMember's manual scaffold.
//
// These four are component-path-only (they need ImGui + InspectorPanel host helpers); they are NOT in
// DrawerRegistry.CreatePrimitive (which stays headless). InspectorPanel registers them in its ctor.

// BEvent (UnityEvent-style serialized event): the multi-row listener editor. The component owns the instance
// (a `public BEvent X = new();` field) so it is edited in place, never reassigned. Matches the old arm exactly
// -- BEventEditor.Draw's return was IGNORED (no dirty mark), so this drawer also ignores it and reports false.
public sealed class BEventDrawer : ITypeDrawer {
    public bool CanDraw(Type t) => typeof(BEvent).IsAssignableFrom(t);
    public bool Draw(IProperty p, IInspectorGui gui) {
        BEventEditor.Draw(p.Name, p.Get() as BEvent);
        return false;
    }
}

// AnimationCurve: the interactive curve widget, applied to ANY AnimationCurve member with no per-component
// wiring (mutated in place, reference type; the "Edit" button opens the standalone window). The old arm
// passed state.MarkViewportDirty as the external-edit callback AND marked dirty on a returned true; this
// drawer routes both through the host so behaviour is identical.
public sealed class AnimationCurveDrawer : ITypeDrawer {
    readonly IComponentInspectorHost host;
    public AnimationCurveDrawer(IComponentInspectorHost host) => this.host = host;
    public bool CanDraw(Type t) => typeof(AnimationCurve).IsAssignableFrom(t);
    public bool Draw(IProperty p, IInspectorGui gui) {
        // A null curve member used to fall to the `else` (no value-based match + no registered drawer) and
        // draw the `(AnimationCurve)` disabled row -- reproduce that exactly via Unsupported so the only
        // change is composition, not pixels.
        if (p.Get() is not AnimationCurve curve) { gui.Unsupported(p.ValueType); return false; }
        bool changed = EditorWidgets.CurveEditor(p.Name, curve, host.MarkViewportDirty);
        if (changed) host.MarkViewportDirty();
        return changed;
    }
}

// ColorGradient: the interactive gradient bar -- same auto-apply-to-any-member story as the curve.
public sealed class ColorGradientDrawer : ITypeDrawer {
    readonly IComponentInspectorHost host;
    public ColorGradientDrawer(IComponentInspectorHost host) => this.host = host;
    public bool CanDraw(Type t) => typeof(ColorGradient).IsAssignableFrom(t);
    public bool Draw(IProperty p, IInspectorGui gui) {
        // Same null-member fallback as the curve drawer: a null gradient drew `(ColorGradient)` before.
        if (p.Get() is not ColorGradient gradient) { gui.Unsupported(p.ValueType); return false; }
        bool changed = EditorWidgets.GradientEditor(p.Name, gradient);
        if (changed) host.MarkViewportDirty();
        return changed;
    }
}

// BObject asset slot: the drag-drop + click-to-pick asset slot for any BObject-derived ASSET member
// (Material/Texture2D/Texture3D/Shader/VolumeProfile/Mesh). The rendering + picker logic stays in
// InspectorPanel.DrawAssetSlot (unchanged); this drawer just routes the IProperty's member/owner/type into it
// through the host, so the slot keeps its EXACT current behaviour while flowing through the stack.
public sealed class AssetSlotDrawer : ITypeDrawer {
    readonly IComponentInspectorHost host;
    public AssetSlotDrawer(IComponentInspectorHost host) => this.host = host;
    public bool CanDraw(Type t) => typeof(BObject).IsAssignableFrom(t);
    public bool Draw(IProperty p, IInspectorGui gui) {
        host.DrawAssetSlot(p);
        return false;
    }
}

// editor-rework G1-editor (Rule 1, gap 1 -- the VISIBLE half; the EntityRef/ComponentRef value types +
// serializer round-trip + PropertyCategories.SceneObjectRef classify landed engine-side in ch17). Terminal
// drawer for the serializable scene-object reference value types. Before this, an EntityRef/ComponentRef
// member matched no drawer and fell to TypeDrawerTerminalStep -> gui.Unsupported -> a dead `(EntityRef)` /
// `(ComponentRef)` disabled label (the visible half of the user's "Unity SerializeField object assignment"
// complaint). Now it routes to the host's interactive scene-object slot (current target + drag-onto-slot +
// searchable picker of live scene entities / behaviours), the parallel of AssetSlotDrawer/DrawAssetSlot.
//
// CanDraw keys on the DECLARED value type (the B4 null-safe rule: a struct value type is never null, but the
// terminal resolves by ValueType regardless of the live value, so the slot draws even for a None ref). The
// drawer reports false (no auto-dirty): the host pushes undo + marks dirty itself only on an actual user
// pick / drag / clear, exactly like the asset slot.
public sealed class SceneObjectRefDrawer : ITypeDrawer {
    readonly IComponentInspectorHost host;
    public SceneObjectRefDrawer(IComponentInspectorHost host) => this.host = host;
    public bool CanDraw(Type t) => t == typeof(EntityRef) || t == typeof(ComponentRef);
    public bool Draw(IProperty p, IInspectorGui gui) {
        host.DrawSceneObjectSlot(p);
        return false;
    }
}

// editor-rework G2-editor (Rule 2, the VISIBLE half of the collection round-trip; the engine half --
// SerializeSequence / DeserializeList / DeserializeArray -- landed in ch19, so a List<T>/T[] member now
// round-trips instead of dropping to null). Terminal drawer for a `List<T>` or `T[]` member. Before this it
// matched no drawer and fell to TypeDrawerTerminalStep -> gui.Unsupported -> a dead `(List`1)` / `(...)`
// disabled label (the visible half of the user's "serialize backendi cok sinirli" complaint). Now it routes
// to the host's interactive collection editor (per-element rows, add / remove, each element drawn RECURSIVELY
// by its own terminal drawer -- a List<Vector3> draws Vector3 widgets, a List<Material> draws asset slots, a
// List<EntityRef> draws scene-object slots), the parallel of AssetSlotDrawer/SceneObjectRefDrawer.
//
// Scope (ch20): List<T> + single-dimension arrays. Dictionary<K,V> (ch21) and a DEEP nested-struct element's
// inner-field write-back (G4) are NOT covered here -- a struct element with no registered terminal drawer
// falls to Unsupported per-element exactly as a struct member does today. An element whose type HAS a drawer
// (primitive / enum / math-struct / asset ref / scene-object ref / curve / gradient) edits in full, because a
// list slot is reassigned as a WHOLE element (list[i] = newValue) so even a boxed value-type element writes
// back -- the limitation is only EDITING a struct element's inner fields, not the element itself.
//
// CanDraw keys on the DECLARED value type (List<> closed generic OR array); a string is IEnumerable<char> but
// is never reached (StringDrawer wins by type-equality, and CanDraw excludes it). The drawer reports false (no
// auto-dirty): the host pushes undo + marks dirty itself only on an actual add / remove / element edit, like
// the asset and scene-object slots.
public sealed class CollectionDrawer : ITypeDrawer {
    readonly IComponentInspectorHost host;
    public CollectionDrawer(IComponentInspectorHost host) => this.host = host;
    public bool CanDraw(Type t) {
        if (t == typeof(string)) return false;                          // IEnumerable<char> -- StringDrawer's
        if (t.IsArray && t.GetArrayRank() == 1) return true;            // T[]
        return t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>);  // List<T>
    }
    public bool Draw(IProperty p, IInspectorGui gui) {
        host.DrawCollectionSlot(p);
        return false;
    }
}

// editor-rework G2-editor (ch21): the VISIBLE half of the Dictionary<K,V> round-trip (the engine half --
// SerializeDictionary / DeserializeDictionary -- landed in ch19, so a Dictionary<K,V> member round-trips
// instead of dropping to null). Terminal drawer for a closed-generic `Dictionary<K,V>` member. Before this
// it matched no drawer and fell to TypeDrawerTerminalStep -> gui.Unsupported -> a dead `(Dictionary`2)`
// disabled label. Now it routes to the host's interactive dictionary editor (per-entry rows, Add / Remove,
// each VALUE drawn RECURSIVELY by its own terminal drawer -- a Dictionary<string,int> draws int widgets, a
// Dictionary<string,Material> draws asset slots, a Dictionary<int,EntityRef> draws scene-object slots), the
// parallel of CollectionDrawer.
//
// Registered AFTER CollectionDrawer (last-wins); no conflict -- a Dictionary<,> is neither List<> nor an
// array, so CollectionDrawer.CanDraw returns false for it. Scope (ch21): leaf-key + leaf/asset/scene-ref
// VALUE dictionaries (the common case). KEY is drawn READ-ONLY (Dictionary keys are immutable: editing a key
// in place would have to remove-old + add-new, and a duplicate-key clash needs handling -- deferred). A
// nested-struct key OR value with no registered terminal drawer falls to Unsupported per-cell exactly as a
// struct member does today (G4). The drawer reports false (no auto-dirty): the host pushes undo + marks dirty
// itself only on an actual add / remove / value edit, like CollectionDrawer.
public sealed class DictionaryDrawer : ITypeDrawer {
    readonly IComponentInspectorHost host;
    public DictionaryDrawer(IComponentInspectorHost host) => this.host = host;
    public bool CanDraw(Type t) =>
        t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>);
    public bool Draw(IProperty p, IInspectorGui gui) {
        host.DrawDictionarySlot(p);
        return false;
    }
}

// editor-rework G2-editor (ch21): an IProperty over the VALUE slot of one dictionary entry (dict[key]). Lets
// a dictionary value flow through the SAME terminal drawer resolution as a member / collection element -- an
// int value resolves IntDrawer, a Material value resolves AssetSlotDrawer, an EntityRef value resolves
// SceneObjectRefDrawer -- so the recursion is "draw-a-value", not a per-value type switch (Rule 2). Get reads
// the boxed value for the captured key; Set writes it back through the supplied `assign` delegate (which sets
// dict[key] = newValue and writes the WHOLE dictionary back to the owning member -> ApplyMember multi-select
// broadcast + dirty), so a value edit behaves exactly like a member edit. Has no MemberInfo, so it carries NO
// attributes/range/conditionals (a value cell is a bare value); ValueType is the value type. The sibling
// CollectionElementProperty serves list/array elements; this one serves dictionary values (same shape).
public sealed class DictionaryValueProperty : IProperty {
    readonly Func<object> get;
    readonly Action<object> assign;

    public DictionaryValueProperty(string name, Type valueType, Func<object> get, Action<object> assign) {
        Name = name;
        ValueType = valueType;
        this.get = get;
        this.assign = assign;
    }

    public string Name { get; }
    public string Label => Name;
    public string Tooltip => null;
    public Type ValueType { get; }
    public object Owner => null;

    public object Get() => get();
    public void Set(object value) => assign(value);

    public MemberAttributes Attributes => MemberAttributes.None;
    public (float min, float max)? Range => null;
    public bool IsColor => false;
    public bool Hdr => false;
    public bool HasOverrideToggle => false;
    public bool Overridden { get => false; set { } }
    public bool TryGetSiblingValue(string memberName, out object value) { value = null; return false; }
}

// editor-rework G2-editor: an IProperty over one ELEMENT slot of a collection (list[i] / array[i]). Lets a
// collection element flow through the SAME terminal drawer resolution as a member -- a Vector3 element resolves
// Vector3Drawer, a Material element resolves AssetSlotDrawer, an EntityRef element resolves SceneObjectRefDrawer
// -- so the recursion is "draw-a-value", not a per-element type switch (Rule 2). Get reads the boxed element;
// Set writes it back through the supplied `assign` delegate (which mutates the backing list/array and writes
// the WHOLE collection back to the owning member -> ApplyMember multi-select broadcast + dirty), so an element
// edit behaves exactly like a member edit. Has no MemberInfo, so it carries NO attributes/range/conditionals
// (an element is a bare value); ValueType is the element type.
public sealed class CollectionElementProperty : IProperty {
    readonly Func<object> get;
    readonly Action<object> assign;

    public CollectionElementProperty(string name, Type elementType, Func<object> get, Action<object> assign) {
        Name = name;
        ValueType = elementType;
        this.get = get;
        this.assign = assign;
    }
    // (G3-editor PolymorphicDrawer follows this class at file end -- see its banner.)

    public string Name { get; }
    public string Label => Name;
    public string Tooltip => null;
    public Type ValueType { get; }
    public object Owner => null;

    public object Get() => get();
    public void Set(object value) => assign(value);

    public MemberAttributes Attributes => MemberAttributes.None;
    public (float min, float max)? Range => null;
    public bool IsColor => false;
    public bool Hdr => false;
    public bool HasOverrideToggle => false;
    public bool Overridden { get => false; set { } }
    public bool TryGetSiblingValue(string memberName, out object value) { value = null; return false; }
}

// editor-rework G3-editor (ch23): the VISIBLE half of the [SerializeReference] polymorphism round-trip (the
// engine half -- the $type codec SerializeReferenceInstance / TryDeserializeReferenceInstance -- landed in
// ch22, so a [SerializeReference] abstract/interface member now round-trips its live concrete type). Terminal
// drawer for a member whose DECLARED type is an interface or abstract class (the [SerializeReference] case).
// Before this it matched no drawer (no primitive / enum / math / asset / collection / dict drawer matches an
// interface or abstract type) and fell to TypeDrawerTerminalStep -> gui.Unsupported -> a dead `(IDamageModifier)`
// / `(StatusEffect)` disabled label. Now it routes to the host's polymorphic slot: a concrete-type DROPDOWN
// (TypeCache.GetTypesDerivedFrom(declaredType) + "None"); picking one Activator.CreateInstance's it (None ->
// null); a set value's members are then drawn RECURSIVELY in a foldout, each through its OWN member pipeline (so
// a nested [SerializeReference] member auto-recurses through this same drawer).
//
// CanDraw STRATEGY (declared-TYPE based, the same purely-type contract every sibling drawer uses): an interface
// or abstract declared type -- EXCEPT a BObject-derived ASSET type. ITypeDrawer.CanDraw receives only the Type
// (no member / attribute context -- the DrawerRegistry resolves by type), so [SerializeReference] cannot be
// tested here. That is FINE for the plain-abstract case: an interface / abstract member WITHOUT
// [SerializeReference] classifies Unsupported engine-side (PropertyCategories) and serializes to null, so it
// would otherwise show a dead label -- giving it the implementor dropdown is strictly additive (the dropdown
// lists derived concrete types; with none it shows just "None").
//
// BObject EXCLUSION (bug fix 2026-06-18): the engine's asset base types are ABSTRACT (Texture / Texture2D /
// Texture3D / Mesh / ...; the concrete bodies are backend types like Dx12Texture3D). Those are ASSETS --
// PropertyCategories.Classify maps any BObject to AssetRef, NEVER Polymorphic, regardless of [SerializeReference]
// -- so an abstract-asset member like `Skybox.Cubemap` (Texture3D) must resolve AssetSlotDrawer, not this one.
// But AssetSlotDrawer keys on `IsAssignableFrom(BObject)` (not a concrete-type test), and this drawer was matching
// EVERY abstract type via last-wins, stealing those members and expanding the backend object's internal fields
// (the user-reported "Cubemap opens UID/Type/Sky Ambient/..."). Excluding BObject here lets the asset slot win
// and keeps polymorphism to the genuine [SerializeReference] interface/abstract-NON-asset case. (The comment's
// old claim that "asset drawers key on concrete types" was wrong -- they key on the BObject base.)
//
// A concrete base WITH [SerializeReference] (the rarer Polymorphic case) is NOT matched here -- a concrete type
// already has its own drawer (Nested member-recursion / math-struct / asset / ...), which keeps its existing
// in-place editing; the type-swap dropdown for a concrete base is deferred (G4). The host owns undo / dirty.
public sealed class PolymorphicDrawer : ITypeDrawer {
    readonly IComponentInspectorHost host;
    public PolymorphicDrawer(IComponentInspectorHost host) => this.host = host;
    // Interface or abstract class declared type, but never a BObject asset (those are AssetSlotDrawer's; an
    // abstract BObject base must not be mistaken for a [SerializeReference] polymorphic slot). Registered last,
    // so this exclusion is what stops it from stealing abstract-asset members from the asset slot via last-wins.
    public bool CanDraw(Type t) =>
        t is { IsInterface: true } or { IsAbstract: true } && !typeof(BObject).IsAssignableFrom(t);
    public bool Draw(IProperty p, IInspectorGui gui) {
        host.DrawPolymorphicSlot(p, p.ValueType);
        return false;
    }
}

// editor-rework G4-editor (ch24): the VISIBLE half of the nested struct/class round-trip (the engine half --
// SerializeNestedInstance / TryDeserializeNestedInstance -- landed in ch24, so a plain nested member now
// round-trips its members + STRUCT inner-field write-back instead of dropping to null). Terminal drawer for a
// member whose declared type is a plain concrete class or a non-primitive struct (PropertyCategory.Nested).
// Before this it matched no drawer (no primitive / enum / math / asset / collection / dict / polymorphic
// drawer matches a plain class/struct) and fell to TypeDrawerTerminalStep -> gui.Unsupported -> a dead
// `(NestedSettings)` / `(NestedConfig)` disabled label. Now it routes to the host's nested slot: a FOLDOUT
// that draws the instance's members RECURSIVELY through the SAME member pipeline (so a nested-in-nested member
// auto-recurses through this same drawer, and a nested [SerializeReference] / collection / ref member resolves
// its own terminal drawer).
//
// CanDraw STRATEGY: delegate to PropertyCategories.Classify (the ENGINE classification the codec keys its
// Nested branch off, so the drawer and serializer agree by construction). Classify == Nested is true for a
// concrete class or non-primitive struct that is NOT a primitive / enum / math struct (Vector*/Quaternion/
// Color) / BObject asset / EntityRef/ComponentRef / collection / dictionary / [SerializeReference]
// abstract-or-interface -- every one of which already has its own terminal drawer. The remaining overlap is
// the editor's special CLASS widgets BEvent / AnimationCurve / ColorGradient (plain classes -> they also
// classify Nested): registering NestedDrawer BEFORE those three (see InspectorPanel ctor) lets their drawers
// WIN by last-registered-wins, so this drawer only ever resolves a member with no dedicated drawer (the true
// fallback). The host owns undo / dirty on an actual inner-field edit.
public sealed class NestedDrawer : ITypeDrawer {
    readonly IComponentInspectorHost host;
    public NestedDrawer(IComponentInspectorHost host) => this.host = host;
    public bool CanDraw(Type t) => PropertyCategories.Classify(t) == PropertyCategory.Nested;
    public bool Draw(IProperty p, IInspectorGui gui) {
        host.DrawNestedSlot(p, p.ValueType);
        return false;
    }
}
