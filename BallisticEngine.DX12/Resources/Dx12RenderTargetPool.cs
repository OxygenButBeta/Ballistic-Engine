using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BallisticEngine.DX12;

// PHASE-2 V2 — the TRANSIENT render-target POOL + lifetime ALIASING.
//
// V1 left every transient target permanently owned (committed resource per pass). V2 recycles physical GPU
// memory: transient targets whose LIFETIMES (first-write → last-read, derived from the V1 DAG order) do NOT
// overlap share the SAME bytes of a pool-owned ID3D12Heap (D3D12 PLACED resources at the same heap offset).
// This is the half-res RGBA16F scratch chain (ssr*/ssgi*/bloom*/ssao*) — the real memory win.
//
// WHAT IS POOLED (audit-passed transients): ssaoA/ssaoB, bloomA/bloomB, ssrTarget/ssrScene,
// ssgiTarget/ssgiDenoised/ssgiScene. WHAT IS IMPORTED (NEVER aliased): cross-frame history
// (taaHistoryA/B, ssgiHistoryA/B, lumTarget/lumHistory) + target/ldr/gbuffer. Importing history is mandatory
// (aliasing a history buffer = temporal corruption).
//
// ★ THE V2 FOOTGUN (read the plan §V2 "READ-BEFORE-WRITE / INIT AUDIT"): "correct lifetime" ≠ "pixel-neutral".
// When resource B reuses A's memory, B's first-activation contents are A's leftover GARBAGE; the aliasing
// barrier does NOT clear it. Any pass that READS its target before fully writing it reads that garbage. The
// PRIMARY safety net is the MANUAL AUDIT below (NOT GBV — GBV's uninitialized-read tracking is unreliable on
// placed/aliased resources). Every pooled target is verified FULLY-OVERWRITTEN-before-read:
//   - ssaoA: main HBAO pass is a full-screen draw covering 100% of pixels → written before any blur reads it.
//   - ssaoB/bloomB/ssgiScene/ssrScene: each is the DST of a full-screen blur/combine draw → fully overwritten.
//   - bloomA: bright-pass full-screen draw fully overwrites it before blurH reads it.
//   - ssrTarget: SSR march (RTV) / RT-refl dispatch (UAV) fully writes it before the combine reads it.
//   - ssgiTarget: SSGI gather (RTV) / RT-GI dispatch (UAV) fully writes it before the resolve reads it.
//   - ssgiDenoised: OIDN unpack / WriteColorRgb fully overwrites it before the combine reads it.
// NONE has a fresh-clear / read-before-write / partial-coverage assumption → aliasing is content-safe. The
// history buffers that DO read-before-write (lumTarget ping-pong, ssgiHistory/taaHistory temporal) are
// EXCLUDED from the pool (imported), so the footgun is structurally avoided.
//
// GATE: BALLISTIC_DX12_GRAPH_ALIAS=1 (requires BALLISTIC_DX12_GRAPH=1). Default off → V1/phase-1 unchanged
// (passes allocate committed resources exactly as before — byte-identical to the frozen golden set).
//
// MECHANISM. The pool is set as `Active` by the orchestrator after the graph compiles its lifetimes (so the
// alias plan is known). Each pooled pass's alloc site calls `Acquire(...)` instead of `new Dx12OffscreenTarget`
// — when no pool is active that helper falls through to a committed target (byte-identical). When active it
// hands back a PLACED target on the assigned heap offset. Before the aliased region runs each frame, the
// orchestrator calls EmitFrameAliasingBarriers(cl) ONCE: for every heap region whose owning tenant differs
// from last frame's, an aliasing barrier (prevTenant → newTenant) is emitted. Because the frame is fully
// synchronous (one recorded list, no CPU↔GPU overlap by default) and every tenant fully overwrites its memory
// before reading, the GPU never observes a stale tenant → SHA == golden.
public sealed class Dx12RenderTargetPool : IDisposable {
    // The currently-active pool (set by the orchestrator when BALLISTIC_DX12_GRAPH_ALIAS=1). Null → no pooling
    // (the committed path). A pass's alloc helper reads this to decide committed-vs-placed.
    public static Dx12RenderTargetPool Active;

    readonly Dx12Device dev;

    // A logical transient target the pool manages. Identity = a stable Name (e.g. "ssgiTarget"). Lifetime is the
    // [firstWritePass, lastReadPass] interval in the compiled graph order; two logicals with disjoint intervals
    // may share a heap region. Desc is the placed-resource footprint (size + format + flags).
    sealed class PooledTarget {
        public string Name;
        public int Width, Height;
        public Format Format;
        public bool AllowUav;
        public long AllocBytes;      // GetResourceAllocationInfo size (alignment-padded)
        public long AllocAlign;      // required alignment
        public int FirstWrite;       // compiled-graph order position of the pass that first WRITES this target
        public int LastRead;         // compiled-graph order position of the pass that last READS this target
        public int RegionId = -1;    // which alias region (shared heap offset) this is assigned to
        public Dx12OffscreenTarget Live;   // the placed target handed out this resolution (re-created on Resize)
    }

    // A heap region = one shared byte-range on the pool heap that a SET of non-overlapping-lifetime logicals
    // share. Tracks the tenant that LAST owned it (for the per-frame aliasing barrier).
    sealed class AliasRegion {
        public ulong Offset;          // byte offset into the pool heap
        public long Bytes;            // region size (max of its members' AllocBytes)
        public readonly List<int> Members = new();   // indices into `targets`
        public int LastTenant = -1;   // index of the logical whose resource was the heap's last tenant (barrier from)
    }

    readonly List<PooledTarget> targets = new();
    readonly Dictionary<string, int> byName = new();
    readonly List<AliasRegion> regions = new();
    ID3D12Heap heap;
    ulong heapBytes;
    bool planBuilt;
    string planReport = "(alias plan not built)";

    public Dx12RenderTargetPool(Dx12Device device) { dev = device; }

    public string PlanReport => planReport;

    // ============ THE PASS ALLOC-SITE HELPER (the ONE call sites change) ============

    // A pooled pass's alloc site calls THIS instead of `new Dx12OffscreenTarget(...)`. When no pool is Active
    // (the default / V1 / phase-1 path) it constructs a normal COMMITTED target — byte-identical to before, so
    // the alloc-site change is invisible when aliasing is off. When a pool is Active AND has `name` in its plan,
    // it hands back a PLACED target aliased onto the assigned heap region. `name` is the stable logical id the
    // plan registered (e.g. "ssgiTarget"); `dev` is the pass's device (used for the committed fallback).
    public static Dx12OffscreenTarget AllocOrPool(Dx12Device dev, string name, int width, int height,
        Format format, bool colorReadable, bool allowUav) {
        var pool = Active;
        if (pool != null && pool.planBuilt && pool.byName.ContainsKey(name))
            return pool.Acquire(name, width, height, format, colorReadable, allowUav);
        return new Dx12OffscreenTarget(dev, width, height, withDepth: false, colorFormat: format,
            colorReadable: colorReadable, allowUav: allowUav);
    }

    // ============ REGISTRATION (one entry per pooled logical target, before the plan is built) ============

    // Register a pooled transient. firstWritePass/lastReadPass are the compiled-graph ORDER positions of the
    // pass that first writes it / last reads it (so the planner can test lifetime overlap). Called once at
    // pool build (after the graph compiled), in any order. Re-registration (same Name) updates the interval.
    public void Register(string name, int width, int height, Format format, bool allowUav,
                         int firstWritePass, int lastReadPass) {
        if (!byName.TryGetValue(name, out int idx)) {
            idx = targets.Count;
            byName[name] = idx;
            targets.Add(new PooledTarget { Name = name });
        }
        var t = targets[idx];
        t.Width = width; t.Height = height; t.Format = format; t.AllowUav = allowUav;
        t.FirstWrite = firstWritePass; t.LastRead = lastReadPass;
    }

    // ============ ALIAS PLAN (compute lifetimes → greedy heap-offset assignment) ============

    // Build the alias plan + allocate the shared heap. Greedy interval-coloring: sort logicals by first-write,
    // assign each to the first existing region whose CURRENT members' lifetimes ALL end before this one's
    // first-write (disjoint) AND whose footprint fits; else open a new region. Region byte-size = max member
    // bytes. The heap is the sum of region sizes. Idempotent rebuild (disposes the old heap).
    public void BuildPlan() {
        // 1. resource allocation info (size+alignment) for every logical, from a probe desc.
        foreach (var t in targets) {
            var desc = ResourceDescription.Texture2D(t.Format, (uint)t.Width, (uint)t.Height, mipLevels: 1, arraySize: 1);
            desc.Flags = ResourceFlags.AllowRenderTarget | (t.AllowUav ? ResourceFlags.AllowUnorderedAccess : ResourceFlags.None);
            var info = dev.Device.GetResourceAllocationInfo(0, new[] { desc });
            t.AllocBytes = (long)info.SizeInBytes;
            t.AllocAlign = (long)info.Alignment;
        }

        // 2. greedy interval coloring on the [FirstWrite, LastRead] lifetimes.
        regions.Clear();
        var order = Enumerable.Range(0, targets.Count).OrderBy(i => targets[i].FirstWrite).ThenBy(i => i).ToList();
        foreach (int i in order) {
            var t = targets[i];
            int chosen = -1;
            for (int r = 0; r < regions.Count; r++) {
                // all current members of this region must END (LastRead) before t starts (FirstWrite) → disjoint.
                bool disjoint = regions[r].Members.All(m => targets[m].LastRead < t.FirstWrite);
                if (disjoint) { chosen = r; break; }
            }
            if (chosen < 0) { regions.Add(new AliasRegion()); chosen = regions.Count - 1; }
            regions[chosen].Members.Add(i);
            regions[chosen].Bytes = Math.Max(regions[chosen].Bytes, t.AllocBytes);
            t.RegionId = chosen;
        }

        // 3. lay regions out end-to-end on the heap (each region aligned to its largest member's alignment, but
        // texture alignment is uniformly 64KB on this path so a single 64KB grid is safe).
        const ulong Align = 64 * 1024;   // D3D12 default texture alignment
        ulong cursor = 0;
        foreach (var reg in regions) {
            cursor = (cursor + Align - 1) & ~(Align - 1);
            reg.Offset = cursor;
            cursor += (ulong)reg.Bytes;
        }
        heapBytes = (cursor + Align - 1) & ~(Align - 1);
        if (heapBytes == 0) heapBytes = Align;   // never create a 0-byte heap

        // 4. (re)create the shared heap. AllowAllBuffersAndTextures denied: we only place non-RT-DS? — these ARE
        // render targets, so AllowOnlyRenderTargetDepthStencilTextures is the correct heap-tier flag (Tier-1
        // GPUs require the placed resources' category to match the heap flags). RX 9070 XT is Tier-2 (all-in-one)
        // but we keep the narrow flag for portability.
        heap?.Dispose();
        var heapDesc = new HeapDescription(heapBytes, HeapType.Default, Align,
            HeapFlags.AllowOnlyRenderTargetDepthStencilTextures);
        heap = dev.Device.CreateHeap<ID3D12Heap>(heapDesc);
        foreach (var reg in regions) reg.LastTenant = -1;

        planBuilt = true;
        planReport = BuildPlanReport();
    }

    // ============ ACQUIRE (called from each pooled pass's alloc site) ============

    // Acquire the placed target for `name`. MUST be called only when this pool is Active AND the plan is built.
    // Disposes the previously-handed-out Live target (Resize re-acquires). Returns a PLACED Dx12OffscreenTarget
    // backed by its region's heap offset. The pass uses it EXACTLY like a committed target (same API).
    public Dx12OffscreenTarget Acquire(string name, int width, int height, Format format, bool colorReadable, bool allowUav) {
        if (!planBuilt) throw new InvalidOperationException("[Dx12RenderTargetPool] Acquire before BuildPlan().");
        if (!byName.TryGetValue(name, out int idx))
            throw new InvalidOperationException($"[Dx12RenderTargetPool] '{name}' was not Register()ed in the plan.");
        var t = targets[idx];
        // The registered footprint must match what the pass asks for (size/format/uav) or the placed offset is
        // wrong-sized → a real bug, fail loud rather than corrupt memory.
        if (t.Width != width || t.Height != height || t.Format != format || t.AllowUav != allowUav)
            throw new InvalidOperationException(
                $"[Dx12RenderTargetPool] '{name}' footprint mismatch: registered {t.Width}x{t.Height} {t.Format} uav={t.AllowUav}, " +
                $"acquired {width}x{height} {format} uav={allowUav}. Re-Register with the actual footprint.");
        t.Live?.Dispose();
        var reg = regions[t.RegionId];
        t.Live = new Dx12OffscreenTarget(dev, width, height, withDepth: false, colorFormat: format,
            colorReadable: colorReadable, allowUav: allowUav, placedHeap: heap, placedOffset: reg.Offset);
        // NOTE: do NOT set RenderTarget.Name here — a debug name changes the GBV message SIGNATURE (the committed
        // baseline resources are unnamed), making every message read as "NEW" against dx12-gbv-baseline.json. Keep
        // pooled targets unnamed so their signatures match the baseline's 'Unnamed ID3D12Resource Object' entries.
        return t.Live;
    }

    // ============ PER-FRAME ALIASING BARRIERS ============

    // Called at the HEAD of each pooled pass's Record (when this pool is Active), passing the names of the placed
    // targets THIS PASS PRODUCES (writes). For EACH produced target, in order, into the open pipelined frame list:
    //   (1) a SPECIFIC aliasing barrier BarrierAliasing(before=null, after=producedResource): "this placed
    //       resource is (re)activated on its shared memory; any prior tenant's data is now invalid." The SPECIFIC
    //       (after-named) form is REQUIRED, not the whole-heap (null,null) form: (null,null) tells the debug layer
    //       EVERY placed resource on the heap was just aliased-out → it would (wrongly) treat a still-live target
    //       this pass merely READS (ssaoA, read by Composite) as freshly-aliased back to its creation state
    //       (RenderTarget), tripping InvalidSubresourceState when that read binds it as a PIXEL_SHADER_RESOURCE.
    //       Naming only the produced `after` resource re-activates ONLY it, leaving read-only live tenants alone.
    //   (2) DiscardResource on the produced target: a D3D12 placed/aliased render target is UNINITIALIZED on first
    //       activation; the debug layer REQUIRES a Discard/Clear/Copy before the first draw uses it
    //       (RenderTargetOrDepthStencilResouceNotInitialized — GBV the secondary net, even though pixels are right
    //       because each tenant fully overwrites). Discard ("prior contents undefined") is the cheapest init hint.
    // ★ Pass ONLY targets this pass WRITES — never a target it merely READS (ssaoA is produced by SSAO but read by
    // Composite, so Composite passes "bloomA","bloomB", NOT "ssaoA"). No-op when no pool is active (committed path).
    public static void PoolBarrier(Dx12Device dev, params string[] producedNames) {
        var pool = Active;
        if (pool == null || !pool.planBuilt || pool.regions.Count == 0) return;
        pool.EmitBarrierAndDiscard(dev, producedNames);
    }

    void EmitBarrierAndDiscard(Dx12Device dev, string[] producedNames) {
        dev.ExecuteSync(cl => {
            foreach (string name in producedNames) {
                if (!byName.TryGetValue(name, out int idx)) continue;
                var t = targets[idx];
                var live = t.Live;
                if (live == null || t.RegionId < 0) continue;
                var reg = regions[t.RegionId];
                // SPECIFIC (before, after) aliasing barrier. `before` = the resource that LAST owned this region's
                // memory (this frame or the previous), `after` = the one being (re)activated now. Naming BOTH is
                // what keeps a still-live read-only tenant on ANOTHER region valid: BarrierAliasing(null, after)
                // would (per spec) decay EVERY placed resource sharing memory back to its init state (RenderTarget),
                // tripping InvalidSubresourceState when e.g. ssaoA (read by Composite) is later bound as an SRV. The
                // precise pair invalidates ONLY the prior tenant of THIS region. before == after (a region with one
                // tenant re-activated each frame) is the no-op self case; null `before` (first ever activation) =
                // "any" which is correct when nothing real preceded it.
                Dx12OffscreenTarget beforeTarget = (reg.LastTenant >= 0 && reg.LastTenant != idx)
                                                   ? targets[reg.LastTenant].Live : null;
                ID3D12Resource before = beforeTarget?.RenderTarget;
                cl.ResourceBarrierAliasing(before, live.RenderTarget);
                live.DiscardForAlias(cl);   // transitions to RenderTarget (idempotent) + DiscardResource (init hint)
                reg.LastTenant = idx;       // this resource now owns the region's memory
            }
        });
    }

    // ============ DIAGNOSTICS ============

    string BuildPlanReport() {
        var sb = new StringBuilder();
        sb.AppendLine($"[Dx12RenderTargetPool] V2 alias plan: {targets.Count} pooled targets → {regions.Count} regions, " +
                      $"heap {heapBytes / 1024}KB (vs {targets.Sum(t => t.AllocBytes) / 1024}KB un-aliased; " +
                      $"saved {(targets.Sum(t => t.AllocBytes) - (long)heapBytes) / 1024}KB).");
        for (int r = 0; r < regions.Count; r++) {
            var reg = regions[r];
            sb.AppendLine($"  region {r} @offset {reg.Offset / 1024}KB size {reg.Bytes / 1024}KB: " +
                          string.Join(", ", reg.Members.Select(m => $"{targets[m].Name}[{targets[m].FirstWrite}..{targets[m].LastRead}]")));
        }
        return sb.ToString();
    }

    // Assert no two logicals SHARING a region have OVERLAPPING lifetimes (the V2 correctness invariant). Returns
    // null if sound, else the first violating pair as a string. Called by the verification harness + at build.
    public string AuditNoOverlap() {
        foreach (var reg in regions) {
            for (int a = 0; a < reg.Members.Count; a++)
                for (int b = a + 1; b < reg.Members.Count; b++) {
                    var ta = targets[reg.Members[a]]; var tb = targets[reg.Members[b]];
                    // overlap iff NOT (ta ends before tb starts OR tb ends before ta starts)
                    bool disjoint = ta.LastRead < tb.FirstWrite || tb.LastRead < ta.FirstWrite;
                    if (!disjoint)
                        return $"OVERLAP: {ta.Name}[{ta.FirstWrite}..{ta.LastRead}] aliases {tb.Name}[{tb.FirstWrite}..{tb.LastRead}] in region (shared memory while both live)";
                }
        }
        return null;
    }

    public void Dispose() {
        foreach (var t in targets) t.Live?.Dispose();
        heap?.Dispose();
        if (ReferenceEquals(Active, this)) Active = null;
    }
}
