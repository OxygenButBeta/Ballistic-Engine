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
