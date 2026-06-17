using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BallisticEngine.DX12;

// The EXECUTOR + (phase-2 V1) COMPILER for the DX12 pass list.
//
// PHASE 1 (the LIST): a registration-ordered list of IRenderPass, sorted ONCE by Dx12RenderPassEvent with a
// STABLE tiebreak, then run per frame (Enabled → Record) in that fixed order. The URP pre-RenderGraph model —
// a sorted, gated, reorderable/injectable pass list. `Execute(ctx)` runs this.
//
// PHASE 2 V1 (the COMPILER): passes DECLARE reads/writes (IRenderPass.Declare → Dx12PassBuilder). Compile()
// builds a dependency DAG, optionally CULLS passes whose outputs nothing consumes (AllowCulling, default OFF),
// and computes a TOPOLOGICAL order. `ExecuteGraph(ctx)` runs THAT order. Barriers stay MANUAL in V1 (the
// phase-1 per-pass head transitions) — the graph only proves it can reproduce the same execution order the
// event-sort produced. Gated behind BALLISTIC_DX12_GRAPH=1 (default off → Execute, the phase-1 list, runs
// unchanged — byte-identical to the frozen golden set).
//
// R1 (load-bearing, BOTH phases): the order MUST be STABLE. Phase-1 Execute uses OrderBy((int)Event) (LINQ
// stable). Phase-2 ExecuteGraph reproduces the SAME order even though a topological order is NOT unique: the
// Kahn ready-queue is a PRIORITY QUEUE keyed (event, registrationIndex) — the identical stable tiebreak. With
// AllowCulling default-OFF and edges that only ever run earlier-in-frame writers before later readers, the
// derived topo order is provably == the event-sort order (so ExecuteGraph is byte-identical to Execute).
public sealed class Dx12RenderGraph {
    readonly List<IRenderPass> registered = new();   // registration order (the stable tiebreak)
    IRenderPass[] ordered = Array.Empty<IRenderPass>();
    bool built;

    // Phase-2 V1 compiler state (built by Compile(); used by ExecuteGraph). Null until first Compile().
    IRenderPass[] graphOrder;                         // the topo order ExecuteGraph runs
    Dx12GraphResources graphResources;
    Dx12PassDeclaration[] declarations;               // per-registered-pass, index-aligned with `registered`
    string lastCompileReport;                         // dump of the compiled DAG/cull/order (diagnostics)

    // Phase-2 V3 (chunk 14): the auto-derived boundary-barrier engine, built by Compile() from each migrated
    // pass's declared Usages. When BarriersDerived is on (BALLISTIC_DX12_GRAPH_BARRIERS=1) ExecuteGraph emits the
    // DERIVED head transition before a migrated pass's Record — replacing the manual head transition the pass
    // removed. Default OFF (BarriersDerived false) → the manual head transitions inside each Record run, unchanged.
    Dx12BarrierDeriver deriver;
    bool barriersDerived;                             // the BALLISTIC_DX12_GRAPH_BARRIERS door (set by SetBarriersDerived)
    string lastDeriverReport;                         // the plan-level manual-vs-derived comparison dump

    // Optional per-pass timing wrapper supplied by the renderer (its TimePass: records GPU wall-time into
    // RenderStats.GpuPasses only when GI timing is on, else just runs the body). Kept as a delegate so the
    // graph doesn't reach into the renderer's RenderStats/GiTimingEnabled internals. Null → run directly.
    readonly Action<string, Action> timePass;

    public Dx12RenderGraph(Action<string, Action> timePass = null) {
        this.timePass = timePass;
    }

    // Register a pass. Call all Add()s once at init (registration order = the stable same-event tiebreak),
    // then Build(). Adding after Build() re-marks the graph dirty so the next Execute re-sorts.
    public void Add(IRenderPass pass) {
        if (pass is null) throw new ArgumentNullException(nameof(pass));
        registered.Add(pass);
        built = false;
        graphOrder = null;   // invalidate the compiled graph too
    }

    // Freeze the phase-1 execution order. Stable: OrderBy keeps registration order within an event. Idempotent.
    public void Build() {
        ordered = registered.OrderBy(p => (int)p.Event).ToArray();   // STABLE — R1
        built = true;
    }

    public IReadOnlyList<IRenderPass> Passes => ordered;
    public int Count => registered.Count;
    public string LastCompileReport => lastCompileReport;

    // ============ PHASE 1 — the event-sorted LIST (Execute) ============

    // Run every enabled pass in the frozen event order. The default path (BALLISTIC_DX12_GRAPH unset) — the
    // proven phase-1 fallback, byte-identical to the golden set.
    public void Execute(Dx12FrameContext ctx) =>
        Execute(ctx, int.MinValue, int.MaxValue);

    // Run only the enabled passes whose Event is in [minEventInclusive, maxEventExclusive). Kept as API (the
    // step-G collapse made the orchestrator call the single full-range Execute(ctx), but the windowed form
    // stays for any future incremental work).
    public void Execute(Dx12FrameContext ctx, int minEventInclusive, int maxEventExclusive) {
        if (!built) Build();
        var list = ordered;
        for (int i = 0; i < list.Length; i++) {
            IRenderPass pass = list[i];
            int ev = (int)pass.Event;
            if (ev < minEventInclusive || ev >= maxEventExclusive) continue;
            if (!pass.Enabled(ctx)) continue;
            if (timePass != null) timePass(pass.Name, () => pass.Record(ctx));
            else pass.Record(ctx);
        }
    }

    // ============ PHASE 2 V1 — the COMPILED graph (Compile + ExecuteGraph) ============

    // Compile the dependency DAG from each pass's declared reads/writes, cull (opt-in), and derive a topo order.
    // Pure CPU bookkeeping — no GPU work, no resource allocation (V1 maps handles 1:1 to existing concrete
    // targets). Call once after Build(); idempotent (recompiles from scratch). Stores a human-readable report.
    public void Compile() {
        if (!built) Build();
        int n = registered.Count;

        // --- 1. collect declarations (run Declare on each pass through a shared-registry builder) ---
        graphResources = new Dx12GraphResources();
        var builder = new Dx12PassBuilder(graphResources);
        declarations = new Dx12PassDeclaration[n];
        for (int i = 0; i < n; i++) {
            var decl = new Dx12PassDeclaration();
            builder.Begin(decl);
            registered[i].Declare(builder);   // a pass that overrides Declare records ≥1 read/write/touch here
            decl.Declared = decl.Reads.Count + decl.Writes.Count + decl.SharedState.Count > 0;
            declarations[i] = decl;
        }

        // --- 2. OPAQUE-NODE edge rule (plan §V1): an opaque (Declare-not-overridden) node imports EVERYTHING —
        // it reads every resource that any declared pass touches, so the culler can never drop a producer that
        // feeds it. Materialize those read-edges by adding every live resource id to the opaque node's Reads. ---
        var liveResourceIds = new HashSet<int>();
        for (int i = 0; i < n; i++) {
            foreach (int id in declarations[i].Reads) liveResourceIds.Add(id);
            foreach (int id in declarations[i].Writes) liveResourceIds.Add(id);
        }
        for (int i = 0; i < n; i++)
            if (declarations[i].IsOpaque)
                foreach (int id in liveResourceIds) declarations[i].Reads.Add(id);

        // --- 3. build DAG edges by walking the passes in EVENT ORDER (= the canonical phase-1 frame order,
        // OrderBy((int)Event) stable on registration index — NOT registration order). This is LOAD-BEARING:
        // registration order ≠ event order (e.g. Fog is registered at index 4 but runs at event 550, AFTER
        // Transparents reg-5/event-450 and GI reg-6/event-500). If the last-writer chain were built in
        // REGISTRATION order, a SceneColor RMW chain would serialize Fog BEFORE Transparents/GI — an edge that
        // CONTRADICTS the (event, registrationIndex) tiebreak and forces a legal-but-WRONG topo order (proven:
        // fog-on diverged from phase-1 until this fix — exactly the R1/R-NEW-8 "topo order is not unique" trap).
        // Walking in event order makes every producer→consumer edge point in the same direction as the frame's
        // canonical order, so the PQ-keyed topo-sort reproduces phase-1 exactly.
        //
        // For each pass (event order): a READ of a handle depends on the last pass that WROTE it (read-after-
        // write); a WRITE depends on the last writer AND the last readers (write-after-write / write-after-read)
        // so RMW chains serialize. Plus shared-state ordering edges (R-NEW-8 (a)): a pass that Touches a key
        // depends on the prior toucher. adj/indeg are indexed by REGISTRATION index (for the PQ + cull); only the
        // WALK is in event order. ordered[] is the event-sorted IRenderPass[]; regOf maps it back to reg index.
        var adj = new List<int>[n];                  // adj[p] = passes that depend ON p (p must run before them)
        var indeg = new int[n];
        for (int i = 0; i < n; i++) adj[i] = new List<int>();
        var lastWriter = new Dictionary<int, int>(); // resource id → last pass reg-index that wrote it
        var lastReaders = new Dictionary<int, List<int>>(); // resource id → reg-indices that read since last write
        var lastToucher = new Dictionary<string, int>();    // shared-state key → last pass reg-index that touched it
        void AddEdge(int from, int to) {
            if (from == to) return;
            if (adj[from].Contains(to)) return;      // dedupe (small lists, fine)
            adj[from].Add(to); indeg[to]++;
        }
        // map an event-ordered position back to the pass's REGISTRATION index (its position in `registered`).
        int RegIndexOf(IRenderPass p) { for (int k = 0; k < n; k++) if (ReferenceEquals(registered[k], p)) return k; return -1; }
        foreach (IRenderPass pass in ordered) {       // EVENT ORDER walk (phase-1 canonical order)
            int i = RegIndexOf(pass);                 // reg index — the identity adj/indeg/PQ use
            var d = declarations[i];
            foreach (int id in d.Reads) {             // read-after-write
                if (lastWriter.TryGetValue(id, out int w)) AddEdge(w, i);
                if (!lastReaders.TryGetValue(id, out var rl)) { rl = new List<int>(); lastReaders[id] = rl; }
                rl.Add(i);
            }
            foreach (int id in d.Writes) {            // write-after-write + write-after-read
                if (lastWriter.TryGetValue(id, out int w)) AddEdge(w, i);
                if (lastReaders.TryGetValue(id, out var rl)) foreach (int r in rl) AddEdge(r, i);
                lastWriter[id] = i;
                lastReaders[id] = new List<int>();    // a write ends the read epoch
            }
            foreach (string key in d.SharedState) {   // shared-state serialization (R-NEW-8 (a))
                if (lastToucher.TryGetValue(key, out int t)) AddEdge(t, i);
                lastToucher[key] = i;
            }
        }

        // --- 4. CULL (opt-in per pass via AllowCulling, default OFF). A pass is cullable only if: it opted in,
        // it is not opaque, it touches no shared state, and EVERY resource it writes is (a) non-imported AND
        // (b) has no surviving consumer. Iterate to a fixpoint (culling a consumer can free its producer).
        // Imported writes (history/scene-color/ldr/g-buffer) ALWAYS keep a pass (their contents matter even with
        // no in-frame reader). Default-OFF means this path is exercised ONLY by a pass that opts in. ---
        var culled = new bool[n];
        bool changed = true;
        while (changed) {
            changed = false;
            for (int i = 0; i < n; i++) {
                if (culled[i]) continue;
                var d = declarations[i];
                if (!d.AllowCulling || d.IsOpaque || d.SharedState.Count > 0) continue;
                if (d.Writes.Count == 0) continue;   // a pass with no declared writes but AllowCulling: leave it
                bool anyKept = false;
                foreach (int id in d.Writes) {
                    if (graphResources.ById(id).Imported) { anyKept = true; break; }
                    // surviving consumer? any non-culled pass that reads this id and depends on us
                    foreach (int consumer in adj[i])
                        if (!culled[consumer] && declarations[consumer].Reads.Contains(id)) { anyKept = true; break; }
                    if (anyKept) break;
                }
                if (!anyKept) { culled[i] = true; changed = true; }
            }
        }

        // --- 5. TOPO-SORT (Kahn) with a PRIORITY QUEUE keyed (event, registrationIndex) so the derived order
        // reproduces phase-1's stable order exactly (R1 extends to V1: a topo order is not unique). Skip culled
        // passes (and decrement their out-edges so survivors still flow). ---
        var indegLive = (int[])indeg.Clone();
        for (int i = 0; i < n; i++)
            if (culled[i]) foreach (int to in adj[i]) indegLive[to]--;   // remove culled producers' edges
        // PQ priority = (event, registrationIndex); registrationIndex == the pass's index i (Add order).
        var ready = new PriorityQueue<int, long>();
        long Key(int i) => ((long)(int)registered[i].Event << 32) | (uint)i;
        for (int i = 0; i < n; i++)
            if (!culled[i] && indegLive[i] == 0) ready.Enqueue(i, Key(i));
        var order = new List<IRenderPass>(n);
        var orderIdx = new List<int>(n);
        int produced = 0;
        while (ready.Count > 0) {
            int i = ready.Dequeue();
            order.Add(registered[i]); orderIdx.Add(i); produced++;
            foreach (int to in adj[i]) {
                if (culled[to]) continue;
                if (--indegLive[to] == 0) ready.Enqueue(to, Key(to));
            }
        }
        int liveCount = 0; for (int i = 0; i < n; i++) if (!culled[i]) liveCount++;
        if (produced != liveCount)
            throw new InvalidOperationException(
                $"[Dx12RenderGraph] DAG has a CYCLE — topo-sort produced {produced} of {liveCount} live passes. " +
                "A pass's declared reads/writes formed a dependency loop (check Declare()).");

        graphOrder = order.ToArray();
        lastCompileReport = BuildReport(orderIdx, culled);

        // --- 6. PHASE-2 V3 (chunk 14): build the auto-derived boundary-barrier engine. For each pass that opted
        // into BarriersDerived (builder.DeriveBarriers), register its ordered Usages → the deriver computes the
        // (role → final-state) map and the runtime emit path. Then run the PLAN-LEVEL defense: compare the derived
        // set to the static manual reference (CompareToManual: derived ⊇ manual + same final state) and THROW at
        // init on any mismatch — a derivation bug surfaces here, not as mid-frame corruption. Pure CPU; the engine
        // only EMITS at runtime when the door is on AND the pass is migrated (ExecuteGraph). ---
        deriver = new Dx12BarrierDeriver();
        for (int i = 0; i < n; i++)
            if (declarations[i].BarriersDerived)
                deriver.Register(registered[i].Name, declarations[i].Usages);
        lastDeriverReport = deriver.CompareToManual(Dx12BarrierDeriver.ManualReference(), out bool unsound);
        if (unsound)
            throw new InvalidOperationException(
                "[Dx12RenderGraph] V3 barrier derivation UNSOUND — derived set does not cover the manual reference " +
                "(same final state per role required). See report:\n" + lastDeriverReport);
    }

    // The BALLISTIC_DX12_GRAPH_BARRIERS door (resolved once by the renderer; requires the GRAPH path). When ON,
    // ExecuteGraph emits each migrated pass's DERIVED head transition before its Record (the pass removed its
    // manual head transition). OFF → migrated passes have NO head transition AT ALL (it was removed) UNLESS this
    // is on — so a migrated pass under GRAPH=1 without BARRIERS=1 would be wrong. Guarded: the migration removes a
    // pass's manual head transition ONLY in tandem with this; default off keeps the comparison/dump but does not
    // emit (the door gates EMIT, not the build). The renderer keeps GRAPH_BARRIERS requiring GRAPH=1.
    public void SetBarriersDerived(bool on) => barriersDerived = on;
    public string LastDeriverReport => lastDeriverReport;

    // Run the COMPILED topo order (the BALLISTIC_DX12_GRAPH=1 path). Compiles lazily on first call. Same
    // Enabled→Record contract as Execute; barriers are still the manual phase-1 head transitions in V1.
    public void ExecuteGraph(Dx12FrameContext ctx) {
        if (graphOrder == null) Compile();
        var list = graphOrder;
        for (int i = 0; i < list.Length; i++) {
            IRenderPass pass = list[i];
            if (!pass.Enabled(ctx)) continue;
            // PHASE-2 V3 (chunk 14): when the barriers door is on, EMIT the migrated pass's DERIVED boundary head
            // transition before Record (the pass removed its manual head transition; ctx.BarriersDerived tells it
            // to skip the manual one). A no-op for un-migrated passes (deriver.Emit returns silently) and when the
            // door is off (the pass emits its own manual head transition inside Record, as in V1/V2).
            if (barriersDerived) deriver.Emit(pass.Name, ctx);
            if (timePass != null) timePass(pass.Name, () => pass.Record(ctx));
            else pass.Record(ctx);
        }
    }

    // PHASE-2 V2: the compiled topo order (the same order ExecuteGraph runs). Null until Compile(). The
    // transient-RT pool reads pass ORDER positions off this to compute each pooled target's lifetime (a pass-
    // private scratch target's lifetime is exactly its owning pass's order position — see Dx12RenderTargetPool).
    public IReadOnlyList<IRenderPass> GraphOrder => graphOrder;

    // The compiled-order position of the pass named `name` (case-sensitive, matches IRenderPass.Name), or -1.
    // Used by the V2 alias planner to stamp each pooled target's owning-pass order index as its lifetime point.
    public int OrderIndexOf(string name) {
        var list = graphOrder;
        if (list == null) return -1;
        for (int i = 0; i < list.Length; i++) if (list[i].Name == name) return i;
        return -1;
    }

    string BuildReport(List<int> orderIdx, bool[] culled) {
        var sb = new StringBuilder();
        sb.AppendLine($"[Dx12RenderGraph] V1 compile: {registered.Count} passes, {graphResources.Count} resources.");
        sb.AppendLine("  Resources: " + string.Join(", ", graphResources.All.Select(r => r.ToString())));
        sb.AppendLine("  Topo order (event/regIdx):");
        foreach (int i in orderIdx) {
            var d = declarations[i];
            string kind = d.IsOpaque ? "opaque" : (d.AllowCulling ? "cullable" : "declared");
            sb.AppendLine($"    {registered[i].Name} [{registered[i].Event}={(int)registered[i].Event}] {kind} " +
                          $"R={d.Reads.Count} W={d.Writes.Count}" + (d.SharedState.Count > 0 ? $" shared={d.SharedState.Count}" : ""));
        }
        for (int i = 0; i < registered.Count; i++)
            if (culled[i]) sb.AppendLine($"  CULLED: {registered[i].Name} (no live consumer for its non-imported writes)");
        return sb.ToString();
    }

    // Fan out a resolution change to every pass that owns resolution-dependent targets. Registration order
    // (NOT the event order) so it matches the original AllocateResolutionTargets call sequence (R5). Empty
    // graph → no-op.
    public void Resize(int width, int height) {
        for (int i = 0; i < registered.Count; i++)
            registered[i].Resize(width, height);
    }
}
