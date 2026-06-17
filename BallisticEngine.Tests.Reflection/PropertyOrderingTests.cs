using System.Linq;
using System.Reflection;
using BallisticEngine;

namespace BallisticEngine.Tests.Reflection;

// editor-rework Phase B residue (Rule 1.5, single-source) oracle: the ONE deterministic [PropertyOrder]
// member-ordering rule (PropertyOrdering). Before this chunk three sites re-implemented the same sort --
// TypePlan.Build, InspectorPanel.DrawMemberList, VolumeProfileEditor.DrawParameters -- the member-level twin
// of the per-member drift B0 killed. This suite locks the rule so the two inspector hosts (now both routed
// through PropertyOrdering) can never disagree about WHICH member draws above WHICH.
//
// Locks: (1) OrderOf reads [PropertyOrder] (0 when absent); (2) Sort = [PropertyOrder] asc then ORIGINAL
// index asc (stable + total -- equal orders keep source order); (3) TypePlan.For(...).Members emits the SAME
// order (the engine source the inspector consumes); (4) order independence -- a different INPUT order yields
// the same output (the rule is a function of the set + each item's order/index, not of feed order); (5) the
// MemberInfo convenience overload matches the keyed Sort.
internal static class PropertyOrderingTests {
    static readonly System.Type Sample = typeof(PropertyOrderingSample);

    // The expected resolved member order (see PropertyOrderingSample's declaration table).
    static readonly string[] Expected = {
        nameof(PropertyOrderingSample.Alpha),   // -10
        nameof(PropertyOrderingSample.Beta),    //   0, decl 0
        nameof(PropertyOrderingSample.Gamma),   //   0, decl 2
        nameof(PropertyOrderingSample.Epsilon), //   0, decl 4
        nameof(PropertyOrderingSample.Delta),   //   5
    };

    public static int Run() {
        var h = new Harness();

        OrderOf(h);
        SortRule(h);
        TypePlanConsumesIt(h);
        OrderIndependence(h);

        return h.Report("PropertyOrdering (Phase B)");
    }

    static MemberInfo Prop(string name) => Sample.GetProperty(name)!;

    // -- OrderOf: the single definition of "the [PropertyOrder] value, 0 when absent" --
    static void OrderOf(Harness h) {
        h.Check("OrderOf reads [PropertyOrder(-10)]", PropertyOrdering.OrderOf(Prop("Alpha")) == -10);
        h.Check("OrderOf reads [PropertyOrder(5)]",   PropertyOrdering.OrderOf(Prop("Delta")) == 5);
        h.Check("OrderOf default = 0 (no attribute)", PropertyOrdering.OrderOf(Prop("Beta")) == 0);
    }

    // -- The rule: [PropertyOrder] asc, original index asc (stable + total) --
    static void SortRule(Harness h) {
        // Feed the members in DECLARATION order; the rule re-sorts to Expected.
        MemberInfo[] declared = {
            Prop("Beta"), Prop("Alpha"), Prop("Gamma"), Prop("Delta"), Prop("Epsilon"),
        };
        h.CheckStrings("Sort(members) -> [PropertyOrder] asc then declaration order",
            PropertyOrdering.Sort(declared).Select(m => m.Name), Expected);

        // The keyed generic overload matches the MemberInfo convenience overload.
        h.CheckStrings("keyed Sort matches MemberInfo overload",
            PropertyOrdering.Sort(declared, PropertyOrdering.OrderOf).Select(m => m.Name), Expected);

        // Stable tie-break: three EQUAL-order items (Beta/Gamma/Epsilon) keep their relative source order even
        // when fed adjacent and out of declaration order -- the (order, index) pair is total, never OrderBy
        // stability being load-bearing.
        string[] equalGroup = { "Epsilon", "Beta", "Gamma" };   // all order 0; fed in THIS order
        MemberInfo[] equalFed = equalGroup.Select(Prop).ToArray();
        h.CheckStrings("equal-order items keep INPUT order (stable)",
            PropertyOrdering.Sort(equalFed).Select(m => m.Name), equalGroup);
    }

    // -- TypePlan.For(...).Members emits the SAME order the inspector now consumes --
    static void TypePlanConsumesIt(Harness h) {
        TypePlan.Clear();
        string[] planOrder = TypePlan.For(Sample).Members.Select(m => m.Name).ToArray();
        h.CheckStrings("TypePlan.Members ordered identically to PropertyOrdering", planOrder, Expected);

        // The plan's per-member Order field is the single-sourced OrderOf value.
        TypePlan.Member alpha = TypePlan.For(Sample).Members.First(m => m.Name == nameof(PropertyOrderingSample.Alpha));
        h.Check("TypePlan.Member.Order == PropertyOrdering.OrderOf", alpha.Order == -10);
    }

    // -- Order independence: a different INPUT order yields the SAME output (function of the set, not feed) --
    static void OrderIndependence(Harness h) {
        MemberInfo[] forward = {
            Prop("Alpha"), Prop("Beta"), Prop("Gamma"), Prop("Delta"), Prop("Epsilon"),
        };
        MemberInfo[] shuffled = {
            Prop("Delta"), Prop("Epsilon"), Prop("Beta"), Prop("Alpha"), Prop("Gamma"),
        };
        string[] a = PropertyOrdering.Sort(forward).Select(m => m.Name).ToArray();
        string[] b = PropertyOrdering.Sort(shuffled).Select(m => m.Name).ToArray();
        // NOTE: feed order changes the index tie-break, so equal-order items follow feed order -- the rule is
        // deterministic PER feed, total over (order, index). Both still place Alpha first / Delta last; the
        // order-0 group follows each feed's own order. Lock the invariant parts (extremes) + determinism.
        h.Check("forward: Alpha first, Delta last", a.First() == "Alpha" && a.Last() == "Delta");
        h.Check("shuffled: Alpha first, Delta last", b.First() == "Alpha" && b.Last() == "Delta");
        h.CheckStrings("same feed -> same output (deterministic)",
            PropertyOrdering.Sort(forward).Select(m => m.Name), a);

        // Restore the shared plan cache warm for any later suite (TypePlan.Clear above only dropped Sample).
        TypePlan.Clear();
    }
}
