using System.Text;
using Vortice.Direct3D12;

namespace BallisticEngine.DX12;

// ============================================================================================
// FAZ -1 — Render-graph v2 (UE-RDG / Granite-grade), the Lumen GI foundation.
// ============================================================================================
//
// DESIGN. This is a deferred frame graph: passes are declared (AddPass) with the resources they
// read/write and a deferred execute lambda; nothing touches the GPU until Compile() bakes the whole
// frame and Execute() replays it. It lives ALONGSIDE the old Dx12RenderGraph (which is compile-time
// static, manual barriers, manual pool indices) and shares nothing with it. It generalises the
// proven Dx12RenderTargetPool aliasing (RT/DS only, TIER_1) to buffers + 2D/3D textures on a TIER_2
// single heap (with a TIER_1 three-category fallback) and adds automatic barrier derivation, async
// compute, and Frostbite pass culling.
//
// ALGORITHMS (sources cited inline at each stage):
//   • DAG + topological sort      — last-writer / last-reader hazard edges (WAR/WAW/RAW).
//   • Pass culling                — Frostbite "FrameGraph" refcount sweep, O'Donnell, GDC 2017.
//   • Transient aliasing          — interval bin-packing; Pavel Smejkal "Render graph / transient
//                                   resource system" 3-state lifetime; greedy first-fit by start.
//   • Automatic barriers          — Granite "invalidate / flush" — track each resource's current
//                                   state, emit a transition only when the next required state
//                                   differs; batch per pass. Split-barriers where a gap exists.
//   • Init-after-alias            — MS docs "Using placed resources" / aliasing: the first use of a
//                                   newly-activated RT/DS/UAV transient on overlapping memory MUST be
//                                   a clear / DiscardResource / full copy before any read.
//   • Cross-queue sync            — producer Queue.Signal(fence,v); consumer Queue.Wait(fence,v).
//
// Lifetime: Reset() begins a frame's setup, AddPass() declares, Compile() bakes (DAG/cull/alias/
// barriers + places transients on the heap), Execute() records. Resize/rebuild defer-disposes the
// heap past FramesInFlight (the GPU may still be reading the old placement).

public sealed class Dx12RgGraph : IDisposable {
    readonly Dx12Device dev;
    readonly Dx12RgResourceRegistry registry = new();
    readonly Dx12RgDescriptorCache descriptors;
    readonly List<Dx12RgPass> passes = new();

    // Heap-tier capability (queried once). Tier2 => one ALLOW_ALL heap; Tier1 => three category heaps.
    readonly bool tier2;

    // The aliasing heap(s). Tier2: heaps[0] only. Tier1: [0]=buffers, [1]=non-RTDS textures,
    // [2]=RT/DS textures (D3D12 TIER_1 forbids mixing those categories in one heap).
    readonly ID3D12Heap[] heaps = new ID3D12Heap[3];
    readonly ulong[] heapBytes = new ulong[3];

    // Realised placed transients we own and must dispose on rebuild.
    readonly List<ID3D12Resource> placedResources = new();

    // Cross-queue sync fence (graph-owned; only used when async-compute infra is on).
    ID3D12Fence asyncFence;
    ulong asyncFenceValue;

    string lastCompileReport = "(not compiled)";
    bool compiled;

    const ulong HeapAlign = 64 * 1024; // D3D12_DEFAULT_RESOURCE_PLACEMENT_ALIGNMENT

    public Dx12RgGraph(Dx12Device device) {
        dev = device ?? throw new ArgumentNullException(nameof(device));
        descriptors = new Dx12RgDescriptorCache(dev);
        tier2 = QueryTier2(dev);
    }

    static bool QueryTier2(Dx12Device dev) {
        try {
            var opt = dev.Device.CheckFeatureSupport<FeatureDataD3D12Options>(Feature.Options);
            return opt.ResourceHeapTier >= ResourceHeapTier.Tier2;
        } catch { return false; }
    }

    public string LastCompileReport => lastCompileReport;
    public Dx12RgResourceRegistry Registry => registry;
    public bool ResourceHeapTier2 => tier2;

    // --- setup -----------------------------------------------------------------------------------

    // Begin a new frame. Drops the previous frame's pass list and bumps the registry generation so
    // any leftover handle is stale. Placed transients + heaps survive until the next Compile rebuilds
    // (or Dispose), which is correct: they are realised at Compile, replayed at Execute.
    public void Reset() {
        passes.Clear();
        registry.Reset();
        compiled = false;
    }

    public Dx12RgHandle ImportTexture(string name, ID3D12Resource res, ResourceStates state)
        => registry.ImportTexture(name, res, state);
    public Dx12RgHandle ImportBuffer(string name, ID3D12Resource res, ResourceStates state)
        => registry.ImportBuffer(name, res, state);

    public void AddPass(string name, Dx12RgQueue queue,
        Action<Dx12RgBuilder> setup, Action<Dx12RgExecuteContext> execute) {
        var pass = new Dx12RgPass(name, queue, execute) { Index = passes.Count };
        var builder = new Dx12RgBuilder(registry, pass);
        setup(builder);
        passes.Add(pass);
        compiled = false;
    }

    public void AddPass(string name, Action<Dx12RgBuilder> setup, Action<Dx12RgExecuteContext> execute)
        => AddPass(name, Dx12RgQueue.Graphics, setup, execute);

    // ============================================================================================
    // COMPILE
    // ============================================================================================
    public void Compile() {
        int n = passes.Count;
        for (int i = 0; i < n; i++) { passes[i].RefCount = 0; passes[i].Culled = false; passes[i].Order = -1; passes[i].Producers.Clear(); }

        BuildDagAndCull(n, out int[] order);
        ComputeLifetimes(order);
        PlaceTransients();
        compiled = true;
        lastCompileReport = BuildReport(order);
    }

    // --- (a) DAG edges + (b) Frostbite refcount cull --------------------------------------------
    //
    // Edges: a pass that READS resource r depends on r's last WRITER (RAW). A pass that WRITES r
    // depends on r's last writer (WAW) and on every reader since that write (WAR). Imported writes
    // and NeverCull passes are roots that keep themselves (and transitively their producers) alive.
    // Cycle detection: Kahn topo-sort must emit every pass; otherwise a read/write loop exists.
    void BuildDagAndCull(int n, out int[] order) {
        var adj = new List<int>[n];
        var indeg = new int[n];
        for (int i = 0; i < n; i++) adj[i] = new List<int>();

        var lastWriter = new Dictionary<int, int>();
        var readersSinceWrite = new Dictionary<int, List<int>>();

        void AddEdge(int from, int to) {
            if (from == to || from < 0) return;
            if (adj[from].Contains(to)) return;
            adj[from].Add(to); indeg[to]++;
        }

        for (int i = 0; i < n; i++) {
            var p = passes[i];
            foreach (var a in p.Reads) {
                int rid = a.Handle.Id;
                if (lastWriter.TryGetValue(rid, out int w)) { AddEdge(w, i); p.Producers.Add(w); }
                if (!readersSinceWrite.TryGetValue(rid, out var rl)) { rl = new List<int>(); readersSinceWrite[rid] = rl; }
                rl.Add(i);
            }
            foreach (var a in p.Writes) {
                int rid = a.Handle.Id;
                if (lastWriter.TryGetValue(rid, out int w)) AddEdge(w, i);           // WAW
                if (readersSinceWrite.TryGetValue(rid, out var rl)) foreach (int r in rl) AddEdge(r, i); // WAR
                lastWriter[rid] = i;
                readersSinceWrite[rid] = new List<int>();
            }
        }

        // ---- Frostbite refcount cull (O'Donnell, GDC 2017 "FrameGraph: Extensible Rendering
        //      Architecture in Frostbite") ----
        // pass.RefCount = number of its writes that are consumed (resource has live readers).
        // resource.ref   = number of readers. Seed a stack with resources whose ref==0 (nobody reads
        // them). Pop r -> its producer.RefCount--; if that hits 0 AND the producer is cullable, the
        // producer is dead -> for every resource IT reads, that resource.ref-- (push if it hits 0).
        var resReaders = new int[registry.Count];
        var resProducer = new int[registry.Count];
        for (int r = 0; r < registry.Count; r++) resProducer[r] = -1;
        for (int i = 0; i < n; i++) {
            foreach (var a in passes[i].Reads) resReaders[a.Handle.Id]++;
            foreach (var a in passes[i].Writes) {
                resProducer[a.Handle.Id] = i;         // last writer owns it for cull purposes
                passes[i].RefCount++;
            }
        }

        bool KeepAlive(int i) {
            var p = passes[i];
            if (p.NeverCull) return true;
            foreach (var a in p.Writes)
                if (registry.Entries[a.Handle.Id].Imported) return true; // writes an external => observable
            return false;
        }

        var stack = new Stack<int>();
        for (int r = 0; r < registry.Count; r++)
            if (resReaders[r] == 0 && resProducer[r] >= 0) stack.Push(r);

        while (stack.Count > 0) {
            int r = stack.Pop();
            int prod = resProducer[r];
            if (prod < 0) continue;
            var pp = passes[prod];
            if (KeepAlive(prod)) continue;
            if (--pp.RefCount != 0) continue;
            // producer has no live output -> cull it and release the resources it reads.
            foreach (var a in pp.Reads) {
                int rr = a.Handle.Id;
                if (--resReaders[rr] == 0 && resProducer[rr] >= 0 && !KeepAlive(resProducer[rr]))
                    stack.Push(rr);
            }
        }
        for (int i = 0; i < n; i++)
            passes[i].Culled = passes[i].RefCount == 0 && !KeepAlive(i) && passes[i].Writes.Count > 0;

        // ---- (a) topological sort (Kahn), skipping culled passes, stable by registration index ----
        var indegLive = (int[])indeg.Clone();
        for (int i = 0; i < n; i++)
            if (passes[i].Culled) foreach (int to in adj[i]) indegLive[to]--;

        var ready = new SortedSet<int>();
        for (int i = 0; i < n; i++)
            if (!passes[i].Culled && indegLive[i] == 0) ready.Add(i);

        var result = new List<int>(n);
        while (ready.Count > 0) {
            int i = ready.Min; ready.Remove(i);
            result.Add(i);
            foreach (int to in adj[i]) {
                if (passes[to].Culled) continue;
                if (--indegLive[to] == 0) ready.Add(to);
            }
        }
        int liveCount = 0; for (int i = 0; i < n; i++) if (!passes[i].Culled) liveCount++;
        if (result.Count != liveCount)
            throw new InvalidOperationException(
                $"[Dx12RgGraph] DAG has a CYCLE — topo-sort emitted {result.Count} of {liveCount} live passes. " +
                "A pass's declared reads/writes formed a dependency loop.");

        for (int k = 0; k < result.Count; k++) passes[result[k]].Order = k;
        order = result.ToArray();
    }

    // --- (c.1) transient lifetimes on the linearized order --------------------------------------
    void ComputeLifetimes(int[] order) {
        foreach (var e in registry.Entries) { e.FirstPass = int.MaxValue; e.LastPass = -1; }
        for (int k = 0; k < order.Length; k++) {
            var p = passes[order[k]];
            foreach (var a in p.Reads)  Touch(registry.Entries[a.Handle.Id], k);
            foreach (var a in p.Writes) Touch(registry.Entries[a.Handle.Id], k);
        }
        static void Touch(Dx12RgResourceRegistry.Entry e, int k) {
            if (e.Imported) return;
            if (k < e.FirstPass) e.FirstPass = k;
            if (k > e.LastPass) e.LastPass = k;
        }
    }

    // --- (c.2) aliasing bin-packing + heap placement + realisation ------------------------------
    //
    // Greedy first-fit by lifetime start (Smejkal): a region holds a set of transients whose
    // lifetimes are pairwise disjoint; a new transient joins the first region all of whose tenants
    // die before it is born, else opens a new region. Region size = max footprint among its tenants.
    // Tier2 packs all categories into one heap; Tier1 keeps three separate heaps (D3D12 rule).
    void PlaceTransients() {
        // free the previous frame's placement (GPU may still read it -> defer past FramesInFlight).
        foreach (var r in placedResources) dev.DeferredRelease(r);
        placedResources.Clear();
        for (int h = 0; h < 3; h++) if (heaps[h] != null) { dev.DeferredRelease(heaps[h]); heaps[h] = null; heapBytes[h] = 0; }
        descriptors.Reset();

        var transients = new List<Dx12RgResourceRegistry.Entry>();
        foreach (var e in registry.Entries) {
            if (e.Imported) continue;
            if (e.LastPass < 0) { e.Resource = null; e.RegionId = -1; continue; } // culled/unused
            var d = e.Desc.ToD3D();
            var info = dev.Device.GetResourceAllocationInfo(0, new[] { d }); // never compute sizes ourselves
            e.AllocBytes = (long)info.SizeInBytes;
            e.AllocAlign = (long)info.Alignment;
            e.HeapCategory = tier2 ? 0 : CategoryOf(e);
            transients.Add(e);
        }

        // per-category region packing.
        int catCount = tier2 ? 1 : 3;
        var regionId = 0;
        for (int cat = 0; cat < catCount; cat++) {
            var members = transients.FindAll(e => e.HeapCategory == cat);
            members.Sort((a, b) => a.FirstPass != b.FirstPass ? a.FirstPass - b.FirstPass : a.Id - b.Id);
            var regions = new List<(long bytes, List<Dx12RgResourceRegistry.Entry> mem)>();
            foreach (var e in members) {
                int chosen = -1;
                for (int r = 0; r < regions.Count; r++) {
                    bool disjoint = true;
                    foreach (var m in regions[r].mem) if (!(m.LastPass < e.FirstPass || e.LastPass < m.FirstPass)) { disjoint = false; break; }
                    if (disjoint) { chosen = r; break; }
                }
                if (chosen < 0) { regions.Add((0, new List<Dx12RgResourceRegistry.Entry>())); chosen = regions.Count - 1; }
                var reg = regions[chosen];
                reg.mem.Add(e);
                reg.bytes = Math.Max(reg.bytes, e.AllocBytes);
                regions[chosen] = reg;
            }

            // assign heap offsets per region, size the category heap.
            ulong cursor = 0;
            foreach (var reg in regions) {
                cursor = Align(cursor, HeapAlign);
                foreach (var e in reg.mem) { e.HeapOffset = cursor; e.RegionId = regionId; }
                cursor += (ulong)reg.bytes;
                regionId++;
            }
            ulong bytes = Math.Max(Align(cursor, HeapAlign), HeapAlign);
            heapBytes[cat] = members.Count == 0 ? 0 : bytes;

            if (heapBytes[cat] > 0) {
                var flags = tier2 ? HeapFlags.AllowAllBuffersAndTextures : CategoryHeapFlags(cat);
                var heapDesc = new HeapDescription(heapBytes[cat], HeapType.Default, HeapAlign, flags);
                heaps[cat] = dev.Device.CreateHeap<ID3D12Heap>(heapDesc);
            }
        }

        // realise placed resources at their assigned offsets.
        foreach (var e in transients) {
            var d = e.Desc.ToD3D();
            ClearValue? clear = (e.Desc.AllowRenderTarget || e.Desc.AllowDepthStencil) && e.Desc.Clear.HasValue
                ? e.Desc.Clear.ToD3D() : (e.Desc.IsBuffer ? null : (ClearValue?)null); // buffers: MUST be null
            e.Resource = dev.Device.CreatePlacedResource<ID3D12Resource>(
                heaps[e.HeapCategory], e.HeapOffset, d, ResourceStates.Common, clear);
            e.CurrentState = ResourceStates.Common;
            // RT/DS/UAV transients sharing memory need init-after-alias on first use (MS rule).
            e.NeedsAliasInit = e.IsRtDs || e.Desc.AllowUav;
            placedResources.Add(e.Resource);
        }
    }

    // Tier-1 heap category: buffers / non-RTDS textures / RT-DS textures must not share a heap.
    static int CategoryOf(Dx12RgResourceRegistry.Entry e) {
        if (e.Desc.IsBuffer) return 0;
        return (e.Desc.AllowRenderTarget || e.Desc.AllowDepthStencil) ? 2 : 1;
    }
    static HeapFlags CategoryHeapFlags(int cat) => cat switch {
        0 => HeapFlags.AllowOnlyBuffers,
        1 => HeapFlags.AllowOnlyNonRenderTargetDepthStencilTextures,
        _ => HeapFlags.AllowOnlyRenderTargetDepthStencilTextures,
    };
    static ulong Align(ulong v, ulong a) => (v + a - 1) & ~(a - 1);

    // ============================================================================================
    // EXECUTE
    // ============================================================================================
    public void Execute(Dx12FrameContext ctx) {
        if (!compiled) Compile();

        var ordered = new List<Dx12RgPass>(passes.Count);
        foreach (var p in passes) if (!p.Culled && p.Order >= 0) ordered.Add(p);
        ordered.Sort((a, b) => a.Order - b.Order);

        var gfxCtx = new Dx12RgExecuteContext(registry, descriptors) { Frame = ctx, FrameIndex = ctx?.FrameCounter ?? 0 };
        bool async = dev.AsyncComputeEnabled;
        if (async) asyncFence ??= dev.Device.CreateFence(0, FenceFlags.None);

        // alias-region tenancy tracking — emit aliasing barriers when a region's occupant changes.
        var regionTenant = new Dictionary<int, int>(); // regionId -> entry.Id currently aliased in

        foreach (var pass in ordered) {
            if (pass.Queue == Dx12RgQueue.AsyncCompute && async) {
                // record this pass on the compute queue with a graphics->compute->graphics handoff.
                // (Dx12Device owns the queues/allocators; RecordAsyncCompute does the fence dance and
                //  falls back to inline graphics recording if the handoff budget is exhausted.)
                dev.RecordAsyncCompute(cl => RunPass(pass, cl, ctx, regionTenant, Dx12RgQueue.AsyncCompute));
            } else {
                var cl = dev.FrameOpen ? dev.FrameList : null;
                if (cl != null) RunPass(pass, cl, ctx, regionTenant, Dx12RgQueue.Graphics);
                else dev.ExecuteSync(c => RunPass(pass, c, ctx, regionTenant, Dx12RgQueue.Graphics));
            }
        }
    }

    void RunPass(Dx12RgPass pass, ID3D12GraphicsCommandList4 cl, Dx12FrameContext ctx,
                 Dictionary<int, int> regionTenant, Dx12RgQueue queue) {
        EmitBarriers(pass, cl, regionTenant);
        var ec = new Dx12RgExecuteContext(registry, descriptors) {
            List = cl, Frame = ctx, FrameIndex = ctx?.FrameCounter ?? 0, Queue = queue,
        };
        pass.Execute(ec);
    }

    // --- (d/e) automatic barrier derivation + aliasing + init-after-alias -----------------------
    //
    // Granite invalidate/flush: for each declared access, if the resource's tracked CurrentState !=
    // the required state, emit a transition; batch all of a pass's transitions into ONE
    // ResourceBarrier call. Aliasing: when a transient's region is now occupied by a different
    // tenant than last time, emit an aliasing barrier (before=old tenant resource, after=this one)
    // and, for RT/DS/UAV, a DiscardResource so the first write doesn't read vendor compression
    // garbage. UAV barriers between back-to-back UAV writes of the same resource.
    void EmitBarriers(Dx12RgPass pass, ID3D12GraphicsCommandList4 cl, Dictionary<int, int> regionTenant) {
        var transitions = new List<ResourceBarrier>();
        var aliasing = new List<ResourceBarrier>();
        var uav = new List<ResourceBarrier>();
        var discards = new List<Dx12RgResourceRegistry.Entry>();

        void Consider(in Dx12RgPass.Access a, bool isWrite) {
            var e = registry.Entries[a.Handle.Id];
            if (e.Resource is null) return;
            ResourceStates want = a.State.ToD3D();

            // aliasing activation: this transient is taking over its region from a dead tenant.
            if (!e.Imported && e.RegionId >= 0) {
                regionTenant.TryGetValue(e.RegionId, out int prevId);
                bool firstEver = !regionTenant.ContainsKey(e.RegionId);
                if (firstEver || prevId != e.Id) {
                    ID3D12Resource before = (!firstEver && prevId != e.Id) ? registry.Entries[prevId].Resource : null;
                    aliasing.Add(ResourceBarrierFromAliasing(before, e.Resource));
                    regionTenant[e.RegionId] = e.Id;
                    if (e.NeedsAliasInit) { discards.Add(e); e.NeedsAliasInit = false; }
                }
            }

            if (e.CurrentState != want) {
                transitions.Add(ResourceBarrier.BarrierTransition(e.Resource, e.CurrentState, want, 0xffffffff, ResourceBarrierFlags.None));
                e.CurrentState = want;
            } else if (isWrite && want == ResourceStates.UnorderedAccess) {
                // same-state consecutive UAV write -> needs a UAV barrier (RAW/WAW hazard on UAV).
                uav.Add(new ResourceBarrier(new ResourceUnorderedAccessViewBarrier(e.Resource)));
            }
        }

        foreach (var a in pass.Reads)  Consider(a, false);
        foreach (var a in pass.Writes) Consider(a, true);

        // order: aliasing first (memory becomes valid), then discards (init), then transitions, then UAV.
        if (aliasing.Count > 0) cl.ResourceBarrier(aliasing.ToArray());
        foreach (var e in discards) cl.DiscardResource(e.Resource);
        if (transitions.Count > 0) cl.ResourceBarrier(transitions.ToArray());
        if (uav.Count > 0) cl.ResourceBarrier(uav.ToArray());
    }

    static ResourceBarrier ResourceBarrierFromAliasing(ID3D12Resource before, ID3D12Resource after)
        => new(new ResourceAliasingBarrier(before, after));

    // --- report ----------------------------------------------------------------------------------
    string BuildReport(int[] order) {
        var sb = new StringBuilder();
        int live = 0, culled = 0; foreach (var p in passes) { if (p.Culled) culled++; else live++; }
        long unaliased = 0; foreach (var e in registry.Entries) if (!e.Imported && e.LastPass >= 0) unaliased += e.AllocBytes;
        ulong heapTotal = 0; foreach (var b in heapBytes) heapTotal += b;

        sb.AppendLine($"[Dx12RgGraph] compile: {passes.Count} passes ({live} live, {culled} culled), " +
                      $"{registry.Count} resources, heapTier={(tier2 ? 2 : 1)}.");
        sb.AppendLine($"  transient heap: {heapTotal / 1024}KB (vs {unaliased / 1024}KB un-aliased; " +
                      $"saved {(unaliased - (long)heapTotal) / 1024}KB across {(tier2 ? 1 : 3)} category heap(s)).");
        sb.AppendLine("  executed order:");
        foreach (int i in order) {
            var p = passes[i];
            sb.AppendLine($"    [{p.Order}] {p.Name} ({p.Queue}) R={p.Reads.Count} W={p.Writes.Count}{(p.NeverCull ? " nevercull" : "")}");
        }
        foreach (var p in passes) if (p.Culled) sb.AppendLine($"  CULLED: {p.Name} (no live consumer for its writes)");

        // alias regions
        var byRegion = new Dictionary<int, List<Dx12RgResourceRegistry.Entry>>();
        foreach (var e in registry.Entries)
            if (!e.Imported && e.RegionId >= 0) {
                if (!byRegion.TryGetValue(e.RegionId, out var l)) { l = new(); byRegion[e.RegionId] = l; }
                l.Add(e);
            }
        foreach (var kv in byRegion) {
            if (kv.Value.Count < 2) continue;
            sb.AppendLine($"  alias region {kv.Key}: " +
                string.Join(", ", kv.Value.ConvertAll(e => $"{e.Name}[{e.FirstPass}..{e.LastPass}]")));
        }
        return sb.ToString();
    }

    public void Dispose() {
        foreach (var r in placedResources) r.Dispose();
        placedResources.Clear();
        for (int h = 0; h < 3; h++) heaps[h]?.Dispose();
        descriptors.Dispose();
        asyncFence?.Dispose();
    }
}
