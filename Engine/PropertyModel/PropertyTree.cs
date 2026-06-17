using System;
using System.Collections.Generic;

namespace BallisticEngine;

// The ROOT of one property-tree instance (editor-rework P0.2): N targets of ONE component/struct type,
// presented as a list of root PropertyNodes (the type's members). This is the object the inspector binds a
// selection to and the serializer walks to emit a component. It pairs the two artifacts: the STATIC TypePlan
// (cached by Type) supplies the member shape; this dynamic tree supplies the live N-target values.
//
// Multi-target is the contract, not a special case: pass 1 target for a single-entity inspect, N for a
// multi-select. Mixed-value + broadcast-write are handled by each PropertyNode, not re-hand-rolled per call
// site (the InspectorPanel.ApplyMember/DrawMixedMarker/MultiTransforms logic collapses into the model).
//
// Lifetime (the DYNAMIC side of the §4 cache boundary): a tree is rebuilt only when the SELECTION or a
// structural shape changes — NOT every frame. The editor caches it keyed by the target object set; the
// nodes themselves rebuild their children only on a polymorphic-type change. "Rebuild the tree every frame"
// is the perf anti-pattern this two-artifact split exists to forbid.
public sealed class PropertyTree {
    public Type TargetType { get; }
    public IReadOnlyList<object> Targets { get; }
    public TypePlan Plan { get; }
    public IReadOnlyList<PropertyNode> Roots { get; }

    PropertyTree(Type targetType, object[] targets, TypePlan plan, PropertyNode[] roots) {
        TargetType = targetType;
        Targets = targets;
        Plan = plan;
        Roots = roots;
    }

    // Build a tree over N targets that MUST all be the same type (the inspector only multi-edits same-type
    // selections; a mismatched target is a caller bug). The active (first) target defines the type/plan.
    public static PropertyTree For(IReadOnlyList<object> targets, int maxDepth = PropertyNode.DefaultMaxDepth) {
        if (targets is null || targets.Count == 0)
            throw new ArgumentException("PropertyTree needs at least one target.", nameof(targets));

        var owners = new object[targets.Count];
        for (int i = 0; i < targets.Count; i++)
            owners[i] = targets[i];

        Type type = owners[0]?.GetType()
            ?? throw new ArgumentException("PropertyTree's first target is null.", nameof(targets));

        TypePlan plan = TypePlan.For(type);
        var roots = new PropertyNode[plan.Members.Count];
        for (int i = 0; i < plan.Members.Count; i++)
            roots[i] = new PropertyNode(plan.Members[i], owners, parent: null, depth: 0, maxDepth);

        return new PropertyTree(type, owners, plan, roots);
    }

    // Convenience for the single-target (single-entity) case — the most common inspect.
    public static PropertyTree For(object target, int maxDepth = PropertyNode.DefaultMaxDepth) =>
        For(new[] { target }, maxDepth);
}
