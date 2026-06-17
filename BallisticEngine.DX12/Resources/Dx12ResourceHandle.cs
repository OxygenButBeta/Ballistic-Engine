namespace BallisticEngine.DX12;

// PHASE-2 (V1) — a VIRTUAL resource handle: the graph's dependency model talks in handles, not in concrete
// Dx12OffscreenTarget references. In V1 a handle still maps 1:1 to ONE existing concrete target (no pooling /
// aliasing yet — that's V2). The handle exists so the compiler can build the dependency DAG, cull, and derive
// an order from declared reads/writes WITHOUT knowing the concrete resource type.
//
// SHARED HANDLE IDENTITY (plan §V1, the one-line design rule that makes the DAG sound): a producer and its
// consumer MUST reference the same physical target through the SAME handle id. Two passes minting separate
// handles for one target form no DAG edge → cull/order go wrong. So handles are NOT created ad-hoc per pass —
// they are MINTED ONCE in a canonical registry (Dx12PassBuilder.Resources, keyed by a stable string name) and
// every pass that touches that target asks the registry for the SAME id. Id is the canonical identity; Name is
// for diagnostics only.
//
// `Imported` marks a resource the graph does NOT own (cross-frame history, the back-buffer, the G-buffer, the
// canonical scene-color target): in V2 it must never be aliased; in V1 it is a documentation/cull hint (an
// imported resource's contents matter even with no in-frame consumer, so a pass writing only imported outputs
// is never culled). All V1 handles are effectively imported (every target is concrete/permanently-owned today).
public readonly struct Dx12ResourceHandle {
    public readonly int Id;        // canonical identity — the ONLY thing the DAG compares
    public readonly string Name;   // diagnostics only (dump/log)
    public readonly bool Imported; // graph does not own it (history/back-buffer/g-buffer/scene-color)

    public Dx12ResourceHandle(int id, string name, bool imported) {
        Id = id; Name = name; Imported = imported;
    }

    public bool IsValid => Id >= 0 && Name is not null;

    public override string ToString() => $"#{Id}:{Name}{(Imported ? "(imported)" : "")}";
}
