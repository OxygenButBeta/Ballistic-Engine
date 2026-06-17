namespace BallisticEngine.DX12;

// A pluggable DX12 render pass — the phase-1 skeleton the phase-2 frame graph upgrades in place.
//
// DELIBERATELY NO command-list parameter (design decision 3, correctness trap 1): the open frame command
// list is threaded IMPLICITLY through `ctx.Dev`/`ctx.Target` (ExecuteSync appends to the frame list on the
// frame thread, exactly as DX12HDRenderer records today). Passing a `cl` would (a) force non-verbatim body
// rewrites when moving each DrawXxx out of the god-object, and (b) break the ExecuteSyncImmediate-based
// passes (DDGI / OIDN readback) that MUST flush the open list mid-record — they own no single `cl`.
//
// Each method maps to one piece of the inline pass it replaces:
//   Enabled  = the VERBATIM outer `if (...)` predicate only — pure, no side effects (R6: every input it
//              reads must be reachable from ctx).
//   Resize   = the pass's AllocXxx body (only passes that OWN resolution-dependent targets do anything).
//   Record   = the DrawXxx body VERBATIM; reaches the open frame list via ctx.Dev.ExecuteSync /
//              ctx.Target.RenderColorOnly, as the inline code does today.
//
// Resource-state transitions move WITH the pass (decision 4): each consumer emits its OWN idempotent head
// transition at the top of Record — NEVER relying on an upstream pass to have left the right state (R2:
// that breaks the moment the upstream is gated off).
public interface IRenderPass {
    // WHEN this pass runs. Dx12RenderGraph sorts by this with a STABLE tiebreak (registration order, R1).
    Dx12RenderPassEvent Event { get; }

    // Display name — used by TimePass (RenderStats.GpuPasses) and the future render-feature inspector.
    string Name { get; }

    // The outer-if predicate, VERBATIM and side-effect-free. The graph skips Record when this is false.
    bool Enabled(Dx12FrameContext ctx);

    // Reallocate resolution-dependent targets. The graph fans this out in registration order, which MUST
    // match the original AllocateResolutionTargets call sequence (R5). Default no-op: most passes own no
    // resolution targets (or own none yet).
    void Resize(int width, int height) { }

    // Record this pass into the open frame command list (via ctx.Dev/ctx.Target — see the no-`cl` note).
    void Record(Dx12FrameContext ctx);

    // PHASE-2 BRIDGE (designed in now so phase 2 is ADDITIVE, not a re-interface). A pass that overrides
    // this declares its resource reads/writes to the frame graph; a pass that does NOT is treated as an
    // opaque / imports-everything node (its manual head transitions stand, it is never culled — the
    // incremental-migration escape hatch). Record stays identical across both phases. Default empty so
    // phase-1 passes need not touch it.
    void Declare(Dx12PassBuilder builder) { }
}

// PHASE-2 placeholder — the builder a pass uses in Declare() to register reads/writes against virtual
// resource handles. Empty in phase 1 (no pass calls it). Phase 2 (V1) fills it with Read/Write/Create
// against a handle layer; defining the type NOW keeps IRenderPass.Declare's signature stable so phase 2
// is purely additive. Do NOT add members until V1 — the empty type is intentional.
public sealed class Dx12PassBuilder {
}
