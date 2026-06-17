using BallisticEngine;

namespace BallisticEngine.Tests.Reflection;

// Sample type exercising the single-sourced [PropertyOrder] member ordering rule (editor-rework Phase B
// residue). Members are DECLARED in one order but carry [PropertyOrder] values that re-sort them, plus a pair
// with EQUAL order to prove the stable declaration-order tie-break. The expected resolved order is asserted in
// PropertyOrderingTests against BOTH TypePlan.For(...).Members (the engine source the inspector now consumes)
// and PropertyOrdering.Sort directly.
//
// Declaration order:           Beta, Alpha, Gamma, Delta, Epsilon
// [PropertyOrder] values:      0,    -10,   0,     5,     0
// Expected ([PropertyOrder] asc, then declaration index asc):
//   Alpha(-10), Beta(0/decl1), Gamma(0/decl3), Epsilon(0/decl5), Delta(5)
public sealed class PropertyOrderingSample {
    // order 0 (default), declared FIRST -- among the order-0 group it must stay ahead of Gamma/Epsilon.
    public int Beta { get; set; }

    // [PropertyOrder(-10)] sorts to the very front regardless of being declared second.
    [PropertyOrder(-10)] public int Alpha { get; set; }

    // order 0, declared third -- keeps declaration order relative to the other order-0 members (after Beta).
    public int Gamma { get; set; }

    // [PropertyOrder(5)] sorts to the very back.
    [PropertyOrder(5)] public int Delta { get; set; }

    // order 0, declared last -- the order-0 group ends with this one (stable tie-break).
    public int Epsilon { get; set; }
}
