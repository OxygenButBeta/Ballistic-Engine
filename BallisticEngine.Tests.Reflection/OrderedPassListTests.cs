using BallisticEngine;

namespace BallisticEngine.Tests.Reflection;

// A2 (editor-rework) substrate contract, tested HEADLESSLY. The editor's EditorFrameGraph / IEditorFramePass
// live in the host (editor) assembly and can't be referenced here, but the ORDERING substrate they ride on —
// OrderedPassList<TPass> — is engine-side pure logic and IS referenceable. This suite proves the load-bearing
// R1 guarantees the frame loop depends on: (1) passes run in ascending event order; (2) equal-event passes
// keep registration order (stable tie-break); (3) the order is independent of registration order across
// runs; (4) the event-window overload runs only the requested slice; (5) an empty list is a no-op; (6) Add
// after Build re-sorts. If these pass, the editor's only remaining job (wiring real pass bodies behind named
// events) is trivial glue — same posture as the A1 menu-registry suite.
internal static class OrderedPassListTests {
    // A minimal stand-in for an editor frame pass: just a name + an event ordinal. Mirrors the shape of
    // IEditorFramePass (Event + Name) without the ImGui Run body.
    sealed record FakePass(string Name, int Event);

    static OrderedPassList<FakePass> NewList() => new(p => p.Event);

    static List<string> RunOrder(OrderedPassList<FakePass> list) {
        var order = new List<string>();
        list.Execute(p => order.Add(p.Name));
        return order;
    }

    public static int Run() {
        var h = new Harness();

        // ── (1)+(2) Ascending event order, stable within an equal event ──────────────────────────────
        // Register DELIBERATELY out of order, with two passes sharing event 100 (Pump, Build) added in
        // that order. Expected run order: events ascending, and the two event-100 passes keep registration
        // order (Pump before Build).
        var list = NewList()
            .Add(new FakePass("Present", 300))
            .Add(new FakePass("Pump", 100))     // event 100, registered first
            .Add(new FakePass("Build", 100))    // event 100, registered second → must follow Pump
            .Add(new FakePass("Throttle", 400))
            .Add(new FakePass("Viewport", 200));
        list.Build();

        h.CheckStrings("ascending event order with stable equal-event tie-break",
            RunOrder(list), "Pump", "Build", "Viewport", "Present", "Throttle");

        // ── (3) Order independent of registration order ──────────────────────────────────────────────
        // Same passes, registered in a different order, but the two event-100 passes still added Pump-then-
        // Build → identical run order. (The tie-break is registration order WITHIN an event, so to keep the
        // sequence identical the equal-event pair must keep the same relative add order; the cross-event
        // passes may be added anywhere.)
        var shuffled = NewList()
            .Add(new FakePass("Throttle", 400))
            .Add(new FakePass("Viewport", 200))
            .Add(new FakePass("Pump", 100))
            .Add(new FakePass("Present", 300))
            .Add(new FakePass("Build", 100));
        h.CheckStrings("cross-event order independent of registration order",
            RunOrder(shuffled), "Pump", "Build", "Viewport", "Present", "Throttle");

        // The frozen Passes view matches the run order (Build is idempotent + lazy via the property).
        h.CheckStrings("Passes view equals run order",
            list.Passes.Select(p => p.Name), "Pump", "Build", "Viewport", "Present", "Throttle");

        // ── (4) Event-window overload runs only the requested slice ──────────────────────────────────
        // [200, 400) → Viewport (200) and Present (300); Pump/Build (100) and Throttle (400) excluded
        // (min inclusive, max exclusive).
        var windowed = new List<string>();
        list.Execute(p => windowed.Add(p.Name), 200, 400);
        h.CheckStrings("event window [200,400) runs Viewport+Present only",
            windowed, "Viewport", "Present");

        // A window that matches nothing runs nothing.
        var empty = new List<string>();
        list.Execute(p => empty.Add(p.Name), 1000, 2000);
        h.Check("empty event window is a no-op", empty.Count == 0);

        // ── (5) Empty list is a guaranteed no-op ─────────────────────────────────────────────────────
        var none = NewList();
        var ran = false;
        none.Execute(_ => ran = true);
        h.Check("empty list Execute is a no-op", !ran);
        h.Check("empty list Count is 0", none.Count == 0);

        // ── (6) Add after Build re-sorts on the next Execute ─────────────────────────────────────────
        var growable = NewList().Add(new FakePass("A", 100)).Add(new FakePass("C", 300));
        growable.Build();
        h.CheckStrings("initial order", RunOrder(growable), "A", "C");
        growable.Add(new FakePass("B", 200));   // slots between A and C after re-sort
        h.CheckStrings("Add after Build re-sorts into the frozen order on next Execute",
            RunOrder(growable), "A", "B", "C");

        // ── Guards: null pass / null run / null eventOf rejected ─────────────────────────────────────
        bool threwNullPass = false;
        try { NewList().Add(null); } catch (ArgumentNullException) { threwNullPass = true; }
        h.Check("Add(null) throws", threwNullPass);

        bool threwNullRun = false;
        try { NewList().Execute(null); } catch (ArgumentNullException) { threwNullRun = true; }
        h.Check("Execute(null) throws", threwNullRun);

        bool threwNullEventOf = false;
        try { _ = new OrderedPassList<FakePass>(null); } catch (ArgumentNullException) { threwNullEventOf = true; }
        h.Check("ctor(null eventOf) throws", threwNullEventOf);

        return h.Report("Ordered pass list (A2)");
    }
}
