using System;
using System.Collections.Generic;
using System.Linq;

namespace BallisticEngine.DX12;

// The phase-1 EXECUTOR: a registration-ordered list of IRenderPass, sorted ONCE by Dx12RenderPassEvent
// with a STABLE tiebreak, then run per frame (Enabled → Record) in that fixed order. This is the URP
// pre-RenderGraph model — a sorted, gated, reorderable/injectable pass list — and the exact structure
// phase 2 upgrades into a true frame graph (the same passes gain Declare()).
//
// R1 (load-bearing): the order MUST be STABLE. List.Sort / Array.Sort are unstable introsort, so two
// same-event passes could swap between frames → intermittent ordering / flicker. We OrderBy((int)Event)
// ONCE at build (LINQ OrderBy is documented-stable, so same-event passes keep registration order) and
// never re-sort per frame. Equivalent to sorting by the composite key (Event, registrationIndex).
public sealed class Dx12RenderGraph {
    readonly List<IRenderPass> registered = new();   // registration order (the stable tiebreak)
    IRenderPass[] ordered = Array.Empty<IRenderPass>();
    bool built;

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
    }

    // Freeze the execution order. Stable: OrderBy keeps registration order within an event. Idempotent.
    public void Build() {
        ordered = registered.OrderBy(p => (int)p.Event).ToArray();   // STABLE — R1
        built = true;
    }

    public IReadOnlyList<IRenderPass> Passes => ordered;
    public int Count => registered.Count;

    // Run every enabled pass in the frozen order. An EMPTY graph (no passes registered — the chunk-3
    // scaffold state) is a guaranteed no-op, so wiring this into BeginRender leaves the image byte-identical
    // while all the inline pass calls still run. Passes are converted one at a time in later chunks.
    public void Execute(Dx12FrameContext ctx) {
        if (!built) Build();
        var list = ordered;
        for (int i = 0; i < list.Length; i++) {
            IRenderPass pass = list[i];
            if (!pass.Enabled(ctx)) continue;
            if (timePass != null) timePass(pass.Name, () => pass.Record(ctx));
            else pass.Record(ctx);
        }
    }

    // Fan out a resolution change to every pass that owns resolution-dependent targets. Registration order
    // (NOT the event order) so it matches the original AllocateResolutionTargets call sequence (R5). Empty
    // graph → no-op.
    public void Resize(int width, int height) {
        for (int i = 0; i < registered.Count; i++)
            registered[i].Resize(width, height);
    }
}
