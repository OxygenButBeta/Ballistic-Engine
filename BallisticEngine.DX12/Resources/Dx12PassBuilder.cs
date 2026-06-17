using System.Collections.Generic;

namespace BallisticEngine.DX12;

// PHASE-2 (V3) — the USAGE of a declared read/write, the input to the auto-barrier derivation. A pass that has
// opted into derived barriers (builder.DeriveBarriers()) declares its SHARED/imported-resource reads with one
// of these instead of the bare Read(handle); the graph maps usage → the idempotent target-state transition the
// pass used to emit as its manual head transition (the usage→state map, plan §V3). Only the boundary transitions
// on SHARED resources (GBuffer depth/color, the canonical SceneColor target) are derived — pass-PRIVATE scratch
// ping-pong transitions (ssaoA, ssrTarget, ssgiTarget) stay inline (they are not pass-boundary head transitions).
public enum Dx12ResourceUsage {
    None = 0,
    GBufferShaderRead,          // gbuffer.ToShaderResource()            — combined PIXEL|NON_PIXEL on ALL colors+depth (Deferred)
    GBufferDepthShaderRead,     // gbuffer.DepthToShaderResource()       — depth → PixelShaderResource (SSAO/AP/Fog/Refl-SSR/FSR)
    GBufferDepthReadOnly,       // gbuffer.DepthToReadOnly()             — depth → DepthRead (Sky/Transparents DSV bind)
    SceneColorShaderRead,       // ctx.SceneColor.ColorToShaderResource()— canonical HDR scene color → PixelShaderResource (GI/Refl/TAA/FSR/Composite)
}

// PHASE-2 (V1) — the builder a pass uses in IRenderPass.Declare() to register its resource reads/writes and
// its scheduling hints against the frame graph. Filled by Dx12RenderGraph.Compile() (one builder per pass,
// once at build), then frozen into a Dx12PassDeclaration the compiler reads to build the dependency DAG.
//
// The chunk-3 scaffold left this an EMPTY placeholder so IRenderPass.Declare's signature was stable; V1 fills
// it in. A pass that does NOT override Declare() declares nothing → it is an OPAQUE node (see Dx12RenderGraph:
// reads every live resource, writes nothing it can be culled on, never culled — the incremental-migration
// escape hatch).
public sealed class Dx12PassBuilder {
    // The CANONICAL resource registry (graph-owned, shared across every pass's builder) — guarantees shared
    // handle identity: Resource("SceneColor") returns the SAME id to producer and consumer so a DAG edge forms.
    public Dx12GraphResources Resources { get; }

    // The declaration being filled for the pass currently calling Declare(). Reset per pass by the compiler.
    internal Dx12PassDeclaration Current { get; private set; }

    internal Dx12PassBuilder(Dx12GraphResources resources) {
        Resources = resources;
    }

    internal void Begin(Dx12PassDeclaration decl) => Current = decl;

    // --- resource handle minting (canonical, shared identity) ---

    // Mint-or-fetch the canonical handle for a named resource. `imported` = graph doesn't own it (history /
    // back-buffer / g-buffer / scene-color); in V1 every target is imported. Call this to get a handle, then
    // Read/Write it. Same name → same id across all passes (the rule that makes the DAG sound).
    public Dx12ResourceHandle Resource(string name, bool imported = true) => Resources.GetOrAdd(name, imported);

    // --- read / write declaration ---

    public void Read(Dx12ResourceHandle handle) => Current.Reads.Add(handle.Id);
    public void Write(Dx12ResourceHandle handle) => Current.Writes.Add(handle.Id);

    // Read-modify-write (the common case for the scene-color chain: every main pass reads `target`, blends, and
    // writes it back). Records BOTH a read and a write so the DAG serializes RMW passes after the prior writer.
    public void ReadWrite(Dx12ResourceHandle handle) { Read(handle); Write(handle); }

    // Convenience overloads (mint + declare in one call).
    public void Read(string name, bool imported = true) => Read(Resource(name, imported));
    public void Write(string name, bool imported = true) => Write(Resource(name, imported));
    public void ReadWrite(string name, bool imported = true) => ReadWrite(Resource(name, imported));

    // --- shared mutable state (R-NEW-8): cbRing / srvVisible / any orchestrator-owned per-frame resource the
    // graph does NOT model as a handle. Two passes that both Touch the same key get a serializing ordering-edge
    // (approach (a), the real fix — closes BOTH the reorder AND the cull hole). A touched pass is ALSO marked
    // non-cullable (belt-and-braces with approach (b)). No converted pass touches cbRing/srvVisible today
    // (grep-verified — they're inline-core/NORT only), so this is dormant in V1; it's baked in now so the graph
    // is sound the moment phase 3 flips the default and a pass starts sharing such state. ---
    public void Touch(string sharedStateKey) {
        Current.SharedState.Add(sharedStateKey);
        Current.AllowCulling = false;   // never cull a pass touching undeclared-shared state (R-NEW-8 (b))
    }

    // --- scheduling hints ---

    // Opt a pass into culling (default OFF for safety — plan §V1: a pass is culled only if it opts in AND nothing
    // consumes its outputs). A pass that writes ONLY non-imported resources nobody reads can be dropped; an
    // imported write, or any consumer, keeps it. Default-OFF means the cull path is exercised only by passes that
    // set this — the matrix MUST include ≥1 cull-enabled pass or the culler footgun ships untested.
    public void AllowCulling() => Current.AllowCulling = true;

    // --- PHASE-2 V3: auto-derived boundary barriers (BALLISTIC_DX12_GRAPH_BARRIERS=1) ---

    // Opt this pass into DERIVED boundary barriers. A pass that calls this declares its SHARED-resource reads via
    // Use(usage) (below); the graph derives the equivalent idempotent head transition and emits it before Record,
    // and the pass REMOVES its own manual head transition. Default OFF (per pass) → the pass keeps its manual head
    // transitions, byte-identical to V1/V2. Migrate ONE pass at a time (plan §V3). DeriveBarriers without any
    // Use() means "this pass needs no boundary transition" (e.g. it only writes pass-private scratch).
    public void DeriveBarriers() => Current.BarriersDerived = true;

    // Declare ONE boundary usage of a SHARED resource. The graph maps usage → the idempotent transition method on
    // the concrete ctx resource and emits it at the pass boundary (replacing the manual head transition). Order
    // matters: usages are emitted in declaration order, BATCHED conceptually at the boundary (the resource objects
    // self-track + early-return, so a redundant one is a free no-op — the manual set's idempotency is preserved).
    public void Use(Dx12ResourceUsage usage) => Current.Usages.Add(usage);
}

// The frozen per-pass declaration the compiler reads. One per registered pass; an opaque (Declare-not-overridden)
// pass has empty Reads/Writes and AllowCulling=false → the compiler treats it as imports-everything/never-culled.
public sealed class Dx12PassDeclaration {
    public readonly HashSet<int> Reads = new();
    public readonly HashSet<int> Writes = new();
    public readonly HashSet<string> SharedState = new();
    public bool AllowCulling;        // false unless the pass opts in via builder.AllowCulling()
    public bool Declared;            // true once the pass overrode Declare() and recorded ≥1 read/write/touch

    // PHASE-2 V3: the ORDERED list of shared-resource boundary usages this pass declared (via builder.Use). The
    // barrier deriver maps each to an idempotent transition on the concrete ctx resource and emits it before
    // Record — replacing the pass's manual head transition. Empty unless the pass opted into BarriersDerived.
    public readonly List<Dx12ResourceUsage> Usages = new();
    public bool BarriersDerived;     // true once the pass called builder.DeriveBarriers() — derive its head transitions

    public bool IsOpaque => !Declared;   // never culled, imports everything (see compiler opaque-edge rule)
}

// The canonical resource registry — one per graph. Maps a stable string name to ONE Dx12ResourceHandle id so
// every pass referencing "SceneColor"/"GBuffer"/etc. shares identity (the DAG-soundness rule).
public sealed class Dx12GraphResources {
    readonly Dictionary<string, Dx12ResourceHandle> byName = new();
    readonly List<Dx12ResourceHandle> all = new();

    public Dx12ResourceHandle GetOrAdd(string name, bool imported) {
        if (byName.TryGetValue(name, out var existing)) return existing;
        var h = new Dx12ResourceHandle(all.Count, name, imported);
        byName[name] = h;
        all.Add(h);
        return h;
    }

    public IReadOnlyList<Dx12ResourceHandle> All => all;
    public int Count => all.Count;
    public Dx12ResourceHandle ById(int id) => all[id];
}
