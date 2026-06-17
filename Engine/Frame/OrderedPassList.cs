using System;
using System.Collections.Generic;
using System.Linq;

namespace BallisticEngine;

// A2 (editor-rework) substrate: a registration-ordered list of named passes, sorted ONCE by an integer
// EVENT key with a STABLE tie-break (registration order), then iterated per frame in that frozen order.
// This is the engine-side, HEADLESS-testable generalization of the renderer's Dx12RenderGraph (which keeps
// the SAME shape in BallisticEngine.DX12). The editor's EditorFrameGraph wraps this to run its frame loop as
// a declared, legible, injectable pass list instead of one 100-line OnRender method.
//
// Why engine-side (not editor-only like the renderer's graph): the editor's pass BODIES are ImGui-coupled
// and unreferenceable from the headless harness (chunk-4/5 precedent), but the ORDERING contract — the load-
// bearing "stable, deterministic, windowed" guarantee — is pure logic. Lifting it here lets the harness test
// it DIRECTLY (not via a hand-rolled mirror), so an ordering regression is caught engine-side.
//
// R1 (load-bearing, copied verbatim from Dx12RenderGraph's reasoning): the order MUST be STABLE. List.Sort /
// Array.Sort are unstable introsort, so two same-event passes could swap between frames → intermittent
// ordering. We OrderBy(Event) ONCE at Build (LINQ OrderBy is documented-stable, so same-event passes keep
// registration order) and never re-sort per frame. Equivalent to sorting by the composite key
// (Event, registrationIndex).
//
// TPass is any pass type; the caller supplies how to read its integer event key (eventOf). The pass's "run"
// is NOT on TPass — the caller passes a run delegate to Execute, so this substrate carries no dependency on
// whatever per-frame context the passes need (the editor threads an EditorFrameContext; a test threads
// nothing). That keeps this type pure + reusable.
public sealed class OrderedPassList<TPass> {
    readonly List<TPass> registered = new();   // registration order — the stable same-event tie-break
    readonly Func<TPass, int> eventOf;
    TPass[] ordered = Array.Empty<TPass>();
    bool built;

    // eventOf maps a pass to its integer event key (e.g. (int)pass.Event). Required.
    public OrderedPassList(Func<TPass, int> eventOf) =>
        this.eventOf = eventOf ?? throw new ArgumentNullException(nameof(eventOf));

    // Register a pass. Call all Add()s once at init (registration order = the stable same-event tie-break),
    // then Build() (or let the first Execute build lazily). Adding after Build() re-marks dirty so the next
    // Execute re-sorts.
    public OrderedPassList<TPass> Add(TPass pass) {
        if (pass is null) throw new ArgumentNullException(nameof(pass));
        registered.Add(pass);
        built = false;
        return this;
    }

    // Freeze the execution order. Stable: OrderBy keeps registration order within an event. Idempotent.
    public void Build() {
        ordered = registered.OrderBy(eventOf).ToArray();   // STABLE — R1
        built = true;
    }

    public IReadOnlyList<TPass> Passes { get { if (!built) Build(); return ordered; } }
    public int Count => registered.Count;

    // Run every pass in the frozen order, applying `run` to each. An empty list is a guaranteed no-op.
    public void Execute(Action<TPass> run) => Execute(run, int.MinValue, int.MaxValue);

    // Run only the passes whose event is in [minEventInclusive, maxEventExclusive). The editor runs the
    // whole frame as one Execute(run); the windowed overload mirrors Dx12RenderGraph's incremental-migration
    // affordance (run a slice around still-inline work) and is what the harness exercises for windowing.
    public void Execute(Action<TPass> run, int minEventInclusive, int maxEventExclusive) {
        if (run is null) throw new ArgumentNullException(nameof(run));
        if (!built) Build();
        TPass[] list = ordered;
        for (int i = 0; i < list.Length; i++) {
            int ev = eventOf(list[i]);
            if (ev < minEventInclusive || ev >= maxEventExclusive) continue;
            run(list[i]);
        }
    }
}
