using System;
using System.Collections.Generic;

namespace BallisticEngine;

// ARTIFACT 2 of the two-artifact cache boundary (editor-rework P0.2): the DYNAMIC property-tree instance.
// One node = one logical value addressed across N TARGETS (the multi-select case is first-class, NOT a
// later add-on — it already worked hand-woven in InspectorPanel.ApplyMember/DrawMixedMarker and both
// reference models, Unity SerializedObject + Odin ValueEntry, are N-target at the property level). A node
// holds the live values; its children are rebuilt ONLY when a polymorphic concrete type changes, never
// every frame (the §4 perf rule). Its STATIC shape comes from the TypePlan; only live state + recursion
// live here.
//
// Cycle/depth safety (Trap 3, tree-only + cycle-guard decision): cycles are detected against the ANCESTOR
// CHAIN (root → this node), by reference identity — so a back-edge (A→B→A) is caught while a DIAMOND (the
// same value reachable via two SIBLING paths) is correctly NOT flagged as a cycle. A back-edge or a node
// past max depth becomes a TERMINAL guard node (drawn "→ already shown", serialized null/dup per Unity)
// instead of recursing forever. The traversal is a TREE with a back-edge guard, never a graph.
public sealed class PropertyNode {
    public const int DefaultMaxDepth = 32;

    // The plan member this node addresses. Carries ValueType/Category/Name/ordering from the static plan.
    public TypePlan.Member Member { get; }

    // The N owner objects this node reads/writes through. For root nodes these are the N selected targets;
    // for a child they are the N parent VALUES (one per target). Length == the tree's target count.
    readonly object[] owners;

    // Parent node (null for a root member). Used to walk the ancestor chain for the cycle guard.
    readonly PropertyNode parent;

    public string Name { get; }
    public Type ValueType { get; }
    public PropertyCategory Category { get; }
    public int Depth { get; }

    // True when this node is the terminal cycle/depth back-edge — not an editable value.
    public bool IsGuard { get; }

    readonly int maxDepth;

    List<PropertyNode> children;        // lazily built; null until first GetChildren()
    object childrenStamp;               // the active value's actual type the children were built for

    // Root/member node ctor.
    internal PropertyNode(TypePlan.Member member, object[] owners, PropertyNode parent, int depth, int maxDepth) {
        Member = member;
        this.owners = owners;
        this.parent = parent;
        Name = member.Name;
        ValueType = member.ValueType;
        Category = member.Category;
        Depth = depth;
        this.maxDepth = maxDepth;
    }

    // Guard-node ctor (terminal back-edge / depth cap).
    PropertyNode(string name, Type valueType, PropertyNode parent, int depth, int maxDepth) {
        Name = name;
        ValueType = valueType;
        Category = PropertyCategory.Unsupported;
        Depth = depth;
        IsGuard = true;
        this.parent = parent;
        owners = Array.Empty<object>();
        this.maxDepth = maxDepth;
    }

    // ── Multi-target value access (the first-class N-target contract) ────────────────────────────────

    // The value of this member on each target, in target order. A null owner (a target whose parent value
    // is null) reads as null. The caller never iterates targets itself — that lived hand-rolled in
    // InspectorPanel and now lives HERE.
    public object[] GetValues() {
        var values = new object[owners.Length];
        for (int i = 0; i < owners.Length; i++)
            values[i] = owners[i] is null ? null : Member.Get(owners[i]);
        return values;
    }

    // The active (first-target) value — what a single widget displays. Mirrors InspectorPanel's "edit the
    // active component, broadcast to the rest". A guard node has no value.
    public object GetValue() =>
        !IsGuard && owners.Length > 0 && owners[0] is not null ? Member.Get(owners[0]) : null;

    // True when the targets DISAGREE on this member's value — Unity's mixed-value dash, first-class in the
    // model (was DrawMixedMarker). Single target → always false.
    public bool HasMultipleValues {
        get {
            if (IsGuard || owners.Length <= 1) return false;
            object first = GetValue();
            for (int i = 1; i < owners.Length; i++) {
                object v = owners[i] is null ? null : Member.Get(owners[i]);
                if (!Equals(v, first)) return true;
            }
            return false;
        }
    }

    public int TargetCount => owners.Length;

    // Broadcast a value to ALL targets (Unity's multi-edit; was ApplyMember). A null owner is skipped; a
    // per-target set that throws (read-only/mismatched on a sibling) is swallowed so one bad target can't
    // abort the whole edit — exactly the existing ApplyMember behaviour.
    public void SetValue(object value) {
        if (IsGuard) return;
        foreach (object owner in owners) {
            if (owner is null) continue;
            try { Member.Set(owner, value); }
            catch { /* mismatched/read-only sibling — skip */ }
        }
        // A structural change (different concrete type) invalidates the lazily-built child plan.
        children = null;
    }

    // ── Recursion (the one traversal both serializer + drawer tree walk) ─────────────────────────────

    // The child nodes for a recursing category (Nested struct/class members; later Polymorphic fields /
    // Collection elements). A leaf category returns empty. Built LAZILY (collapsed/unexpanded nodes cost
    // nothing — §4) and REBUILT only when the active value's actual TYPE changes (the DYNAMIC side of the
    // cache boundary — e.g. a polymorphic field reassigned to a different concrete type).
    public IReadOnlyList<PropertyNode> GetChildren() {
        if (IsGuard) return Array.Empty<PropertyNode>();

        object active = GetValue();
        object stamp = active?.GetType();
        if (children is not null && Equals(stamp, childrenStamp))
            return children;

        children = BuildChildren(active);
        childrenStamp = stamp;
        return children;
    }

    List<PropertyNode> BuildChildren(object active) {
        var result = new List<PropertyNode>();
        // Only structural categories recurse. Today the model recurses Nested (the user's `struct Pair`
        // Rule-2 case) and Polymorphic's instantiated value; it is STRUCTURALLY ready for Collection (G2).
        if (Category is not (PropertyCategory.Nested or PropertyCategory.Polymorphic))
            return result;
        if (active is null)
            return result;

        // Depth cap (Trap 3): stop before the stack does.
        if (Depth + 1 > maxDepth)
            return Guard("(max depth reached)", result);

        // Cycle guard (Trap 3): descending into `active` would build child nodes that OWN `active`. A cycle
        // exists iff `active` is already an OWNER on the path from the root to this node — i.e. some ancestor
        // (or this node, or the root target itself) reads its value THROUGH `active`. Walk this → root and
        // compare against each node's active owner (owners[0]); the root node's owner IS the target, so a
        // self-cycle (target.Self == target) is caught here. Checked against the path only (not a global
        // set) so a DIAMOND (same value via two sibling paths) is NOT flagged. Structs are value types →
        // never reference-cyclic, never checked.
        if (!active.GetType().IsValueType && IsOwnerOnPath(active))
            return Guard("→ already shown", result);

        // Recurse into the child type's plan. Each child addresses N owners = the N parent VALUES (one per
        // target). A target whose parent value is null contributes a null owner (its child reads null).
        TypePlan childPlan = TypePlan.For(active.GetType());
        object[] childOwners = ChildOwners();
        foreach (TypePlan.Member m in childPlan.Members)
            result.Add(new PropertyNode(m, childOwners, this, Depth + 1, maxDepth));

        return result;
    }

    // The instance THIS node reads its active value through (owners[0]). For a root node that's the active
    // target; for a child it's the parent's active value. The chain of these owners root→here is the path.
    object ActiveOwner => owners.Length > 0 ? owners[0] : null;

    // Walk this → root; true if `value` (reference identity) is the active owner of any node on the path —
    // meaning descending into `value` would re-enter an instance already on the path (a back-edge / cycle).
    bool IsOwnerOnPath(object value) {
        for (PropertyNode n = this; n is not null; n = n.parent)
            if (ReferenceEquals(n.ActiveOwner, value))
                return true;
        return false;
    }

    // The per-target parent values that this node's children own: each child owner is THIS node's value on
    // the corresponding target.
    //
    // KNOWN LIMITATION (struct write-back, Phase B/G to close): for a Nested VALUE type, Member.Get boxes a
    // COPY of the struct, so a child node edits the copy, not the parent's field. A class member is a
    // reference, so its child edits propagate. Unity solves struct write-back with a SerializedProperty PATH
    // (re-set the boxed struct up the chain after a leaf edit). The P0.2 contract names the recursion; the
    // write-back path for struct children is wired when B0/G actually drive edits through the tree — the
    // headless harness here verifies struct recursion SHAPE, not struct leaf write-back, so this gap is
    // explicit and tested-around rather than silently shipped.
    object[] ChildOwners() {
        var result = new object[owners.Length];
        for (int i = 0; i < owners.Length; i++)
            result[i] = owners[i] is null ? null : Member.Get(owners[i]);
        return result;
    }

    List<PropertyNode> Guard(string label, List<PropertyNode> into) {
        into.Add(new PropertyNode(label, ValueType, this, Depth + 1, maxDepth));
        return into;
    }
}
