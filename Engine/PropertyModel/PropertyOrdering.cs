using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace BallisticEngine;

// editor-rework Phase B residue (Rule 1.5, single-source): the ONE deterministic [PropertyOrder] member
// ordering rule. Before this, three sites re-implemented the same sort independently -- TypePlan.Build (the
// engine source of truth), InspectorPanel.DrawMemberList (component inspector), and VolumeProfileEditor
// .DrawParameters (volume inspector). Three copies of the same ordering = the exact drift B0 set out to kill,
// just at the member-to-member level instead of the per-member step level. This collapses them onto one pure,
// headless, harness-locked function so the inspector hosts can no longer disagree about WHICH member draws
// above WHICH.
//
// The rule (P0.4 determinism applied to member order, identical to the old TypePlan.Build inline sort):
//   primary  = [PropertyOrder].Order ascending  (default 0 keeps a non-annotated type in declaration order),
//   tie-break = the item's ORIGINAL enumeration index ascending (stable + total -- the SAME order on every
//               machine/build, never reflection's unspecified enumeration re-sorted nondeterministically).
// This is byte-identical to a LINQ `OrderBy(orderKey)` over the source sequence (OrderBy is a stable sort, so
// equal keys keep source order = the original index tie-break), which is what the two editor sites did -- so
// routing them through here is a pure structural MOVE, not a behaviour change.
public static class PropertyOrdering {
    // The [PropertyOrder] value for a member (0 when absent) -- the single definition every site shares so the
    // "default 0 = declaration order" contract lives in one place.
    public static int OrderOf(MemberInfo member) =>
        member.GetCustomAttribute<PropertyOrderAttribute>()?.Order ?? 0;

    // Order an arbitrary sequence by a caller-supplied [PropertyOrder] key, stable in source order. Generic so
    // the component path (orders MemberInfo) and the volume path (orders parameter slots, keyed on slot.Field)
    // share ONE rule. Returns a new ordered array; the input is not mutated.
    public static T[] Sort<T>(IEnumerable<T> items, Func<T, int> orderOf) {
        if (items is null) throw new ArgumentNullException(nameof(items));
        if (orderOf is null) throw new ArgumentNullException(nameof(orderOf));

        // Capture the original index as the explicit, total tie-break -- never rely on OrderBy's stability
        // being load-bearing across refactors; make the (order, index) pair the contract.
        var indexed = items.Select((item, index) => (item, order: orderOf(item), index)).ToList();
        return indexed
            .OrderBy(e => e.order)
            .ThenBy(e => e.index)
            .Select(e => e.item)
            .ToArray();
    }

    // Convenience overload for the common MemberInfo case (component inspector + TypePlan): orders by the
    // member's own [PropertyOrder].
    public static MemberInfo[] Sort(IEnumerable<MemberInfo> members) => Sort(members, OrderOf);
}
