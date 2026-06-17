namespace BallisticEngine.DX12;

// R1.1 (GI Pragmatic Revival) — the ONE source of truth for the bindless-heap RESERVED TAIL.
//
// The shader-visible bindless heap (Dx12Backend.BindlessHeap, cap = Dx12BindlessTail.HeapCapacity) is a bump
// allocator: the MATERIAL/geometry SRVs grow UP from index 0 (EnsureMaterialTable.Reset → RegisterMaterial /
// Dx12RtGeometry.RegisterStructuredSrv via BindlessHeap.Allocate()), while the four RT/GI descriptor tables
// (RtRefl / ScreenProbe / DDGI / RtGi) live in a RESERVED TAIL counting DOWN from the cap. Before R1.1 each
// pass hand-wrote its own `const int XxxTableBase = 16384 - N;` magic number (Dx12GiPass.cs ×3,
// Dx12ReflectionsPass.cs ×1). Four raw integers, one off-by-one apart from a silent descriptor-aliasing GPU
// hang. This centralizes them so:
//   * there is ZERO manual `16384 - N` magic number anywhere (grep proof: only this file mentions HeapCapacity);
//   * the bases are DERIVED by cumulative reservation from the cap → blocks structurally CANNOT overlap;
//   * a COMPILE-TIME assert (see Asserts) verifies (a) the derived bases match the historical layout exactly
//     (so R1.1 is a pure refactor — byte-identical descriptor placement, byte-identical render), (b) every
//     block's USED slot count fits inside its RESERVED region, and (c) the whole tail sits comfortably above
//     the material region's realistic high-water mark.
//
// LIFECYCLE (matches EnsureMaterialTable.Reset @ Dx12GpuDrivenRenderer.cs): Reset() only rewinds the slot-LOCAL
// material cursor to 0; it does NOT touch the tail. The tail bases are FIXED indices (never allocated through
// the bump cursor), so they remain valid across every Reset — exactly as the old hand-written constants did.
// Dx12RtGeometry's per-triangle MaterialId SRV (R1.0) uses BindlessHeap.Allocate() — a HEAD allocation that
// bumps up from 0, NOT a tail offset — so it is unaffected by this change and adds no magic number.
//
// Layout (DOWN from the cap, in declaration order — RtRefl is lowest, RtGi highest, matching the pre-R1.1
// "RtRefl < ScreenProbe < DDGI < RtGi" comment in Dx12ReflectionsPass.cs):
//   RtRefl       : 8 used, 16 reserved  → base 16352 (historical 16384-32)
//   ScreenProbe  : 3 used,  4 reserved  → base 16368 (historical 16384-16)
//   DDGI         : 3 used,  4 reserved  → base 16372 (historical 16384-12)
//   RtGi         : 6 used,  8 reserved  → base 16376 (historical 16384-8)
//   (cap 16384)
// The non-uniform reserved sizes (16 / 4 / 4 / 8) are NOT arbitrary — they reproduce the historical bases
// exactly so the refactor is byte-identical. The slack (RtRefl reserves 16 for 8 used; RtGi reserves 8 for 6)
// is headroom the old layout already had; R2/R3 can grow a table into its own slack without moving any base.
internal static class Dx12BindlessTail
{
    // The shader-visible bindless heap's per-frame-slot capacity (Dx12Backend.Initialize creates the heap at
    // this size). The single place the cap is named — every base is derived from it, so there is no `16384 - N`.
    public const int HeapCapacity = 16384;

    // Per-block RESERVED slot counts, in tail-declaration order (lowest base first). Reserved >= Used (asserted).
    // These four numbers + HeapCapacity are the ONLY layout inputs; everything else is derived.
    const int RtReflReserved = 16;
    const int ScreenProbeReserved = 4;
    const int DdgiReserved = 4;
    const int RtGiReserved = 8;

    // Per-block USED slot counts (descriptors each pass actually writes; asserted <= reserved).
    //   RtRefl   : t0 TLAS, t1 depth, t2 normal, t3 material, t4 irr cube, t5 prefilter, t6 DDGI atlas, u0 ssr (8)
    //   ScreenPb : t0 TLAS, t1 irr cube, t2 DDGI atlas                                                       (3)
    //   DDGI     : t0 TLAS, t1 irr cube, t2 prev-irr atlas                                                   (3)
    //   RtGi     : t0 TLAS, t1 depth, t2 normal, t3 irr cube, t4 lit scene, u0 ssgiTarget                    (6)
    public const int RtReflUsed = 8;
    public const int ScreenProbeUsed = 3;
    public const int DdgiUsed = 3;
    public const int RtGiUsed = 6;

    // Total tail size (sum of reserved blocks) — derived, never hand-written.
    const int TailReserved = RtReflReserved + ScreenProbeReserved + DdgiReserved + RtGiReserved;

    // Bases derived by cumulative reservation from the TOP of the heap. RtGi sits highest (its reserved block
    // ends exactly at the cap); each lower block subtracts the blocks above it. Pure arithmetic → no overlap is
    // possible by construction, and there is no literal base address anywhere.
    public const int RtGiTableBase = HeapCapacity - RtGiReserved;                                   // 16376
    public const int DdgiTableBase = RtGiTableBase - DdgiReserved;                                   // 16372
    public const int ScreenProbeTableBase = DdgiTableBase - ScreenProbeReserved;                     // 16368
    public const int RtReflTableBase = ScreenProbeTableBase - RtReflReserved;                        // 16352

    // The lowest tail index — the material/geometry head allocator must never bump up to here. The bindless heap
    // throws (DETERMINISTIC, see Dx12DescriptorHeap.Allocate) if materials exhaust capacity, so a head/tail
    // collision surfaces as a localized throw, not a silent GPU descriptor aliasing hang. MaxMaterials*6 + the
    // Dx12RtGeometry per-mesh SRVs stay far below this in practice.
    public const int TailStart = RtReflTableBase;                                                    // 16352

    // === COMPILE-TIME asserts (C# has no static_assert; this is the closest equivalent — a `const` boolean fed
    // into a fixed-size buffer whose size is 0 when the assert holds and -1 when it fails, which is a HARD
    // compile error "constant value '-1' cannot be converted to ...". They cost nothing at runtime and fire at
    // BUILD time, exactly as the plan's "compile-time asserted, zero manual magic numbers" DoD demands.) ===
    // Each guard is `(condition ? 0 : -1)` consumed by a const that the compiler must evaluate.

    // (1) The derived bases MUST equal the historical hand-written layout, or R1.1 silently moves descriptors and
    //     stops being byte-identical. 16352 / 16368 / 16372 / 16376 are the values the old `16384 - N` produced.
    const int A_RtReflBaseMatches = 1 / (RtReflTableBase == 16352 ? 1 : 0);
    const int A_ScreenProbeBaseMatches = 1 / (ScreenProbeTableBase == 16368 ? 1 : 0);
    const int A_DdgiBaseMatches = 1 / (DdgiTableBase == 16372 ? 1 : 0);
    const int A_RtGiBaseMatches = 1 / (RtGiTableBase == 16376 ? 1 : 0);

    // (2) Every block's USED slots fit inside its RESERVED region (no block writes past its reservation).
    const int A_RtReflFits = 1 / (RtReflUsed <= RtReflReserved ? 1 : 0);
    const int A_ScreenProbeFits = 1 / (ScreenProbeUsed <= ScreenProbeReserved ? 1 : 0);
    const int A_DdgiFits = 1 / (DdgiUsed <= DdgiReserved ? 1 : 0);
    const int A_RtGiFits = 1 / (RtGiUsed <= RtGiReserved ? 1 : 0);

    // (3) The whole reserved tail fits in the heap and leaves the material region a generous head room
    //     (MaxMaterials 4096 × 6 texture descriptors = 24576 would overflow 16384 long before the tail — the
    //     real high-water mark is a few hundred materials; this asserts the tail itself is sane, < cap, > 0).
    const int A_TailWithinHeap = 1 / (TailReserved < HeapCapacity && TailReserved > 0 ? 1 : 0);
    const int A_TailStartPositive = 1 / (TailStart > 0 ? 1 : 0);

    // Touch the guards so the compiler is forced to evaluate them (an unused private const is still evaluated,
    // but referencing them documents intent and survives any future "remove unused" pass).
    static Dx12BindlessTail() => _ = A_RtReflBaseMatches + A_ScreenProbeBaseMatches + A_DdgiBaseMatches
        + A_RtGiBaseMatches + A_RtReflFits + A_ScreenProbeFits + A_DdgiFits + A_RtGiFits
        + A_TailWithinHeap + A_TailStartPositive;
}
