using System.Reflection;
using BallisticEngine;

namespace BallisticEngine.Tests.Reflection;

// P0.3 (editor-rework Chunk 3) suite: proves the hot-reload invalidation contract drops every stale
// reflection cache at the reload boundary. The bug this guards against is "first hot-reload breaks" —
// a TypeCache query or a compiled TypePlan built over the OLD game-script ALC being served after the ALC
// unloaded and a new assembly loaded.
//
// We can't unload an ALC inside this single-process harness, so we SIMULATE the reload exactly as
// EngineBootstrap.ReloadGameScripts does, in order:
//   (1) caches are warm over [engine + test-assembly]  (the test assembly stands in for game scripts),
//   (2) ReloadCaches.InvalidateAll()                    (the new line in ReloadGameScripts),
//   (3) TypeCache.Build(engine ONLY)                    (BuildComponentRegistry over the NEW set — the
//                                                         test "game-script" types are gone),
// then assert: after (2) every cache is empty/cold, and after (3) only the new universe is served — the
// stale fixtures never reappear. This is the same shape as the TypeCache rebuild test, but driven through
// the CENTRAL contract (one InvalidateAll) instead of TypeCache.Build's own clear, which is the point of
// P0.3: a future window/command/drawer cache rides the same one line.
internal static class ReloadInvalidationTests {
    public static int Run() {
        var h = new Harness();

        Assembly engine = typeof(ComponentRegistry).Assembly;
        Assembly tests = typeof(ReloadInvalidationTests).Assembly;

        // ── The contract is wired at all (module initializers ran) ────────────────────────────────────
        // TypeCache + TypePlan each self-register via [ModuleInitializer]; both assemblies are loaded, so
        // both must be present. Non-zero proves the registration mechanism fired without anyone touching
        // the caches first — the whole reason for [ModuleInitializer] over a lazy static ctor.
        h.Check("ReloadCaches has registered invalidators", ReloadCaches.RegisteredCount >= 2,
            $"expected >=2 (TypeCache + TypePlan), got {ReloadCaches.RegisteredCount}");

        // ── Warm both caches over the full universe ───────────────────────────────────────────────────
        TypeCache.Build(engine, tests);
        h.Check("TypeCache built warm", TypeCache.IsBuilt);
        h.Check("TypeCache sees game-script fixtures before reload",
            TypeCache.GetTypesDerivedFrom<ISample>().Count > 0);

        // Compile a plan for a "game-script" fixture type and confirm it is genuinely cached (same instance
        // returned twice) — so dropping it is observable.
        TypePlan warmPlan = TypePlan.For(typeof(SampleLeaves));
        h.Check("TypePlan caches a fixture plan",
            ReferenceEquals(warmPlan, TypePlan.For(typeof(SampleLeaves))),
            "second For() should return the SAME cached instance");
        h.Check("warm plan has members", warmPlan.Members.Count > 0);

        // ── (2) The reload boundary: ONE central call drains every cache ───────────────────────────────
        ReloadCaches.InvalidateAll();

        // TypeCache: snapshot dropped, IsBuilt false, every query empty — nothing stale can be served in
        // the window between unload and the rebuild that follows.
        h.Check("InvalidateAll drops TypeCache.IsBuilt", !TypeCache.IsBuilt);
        h.Check("InvalidateAll empties TypeCache queries",
            TypeCache.GetTypesDerivedFrom<ISample>().Count == 0 &&
            TypeCache.GetTypesDerivedFrom(typeof(Behaviour)).Count == 0,
            "all queries must return empty after the snapshot is cleared");

        // TypePlan: the cached plan is dropped, so For() recompiles a FRESH instance (not the stale one we
        // held). In a real reload this fresh compile runs over the NEW ALC's `SampleLeaves`; here we only
        // prove the cache was emptied (identity changed) — the mechanism that prevents serving the old plan.
        TypePlan rebuiltPlan = TypePlan.For(typeof(SampleLeaves));
        h.Check("InvalidateAll drops the cached TypePlan",
            !ReferenceEquals(warmPlan, rebuiltPlan),
            "For() after InvalidateAll must recompile, not return the stale cached plan");

        // ── (3) Rebuild over the NEW assembly set (game-script types gone) ────────────────────────────
        // Mirror BuildComponentRegistry running with the engine assembly only (the test "game scripts" no
        // longer in the set). The stale fixtures must NOT come back; engine types must.
        TypeCache.Build(engine);
        h.Check("after reload+rebuild, stale fixtures are gone",
            TypeCache.GetTypesDerivedFrom<ISample>().Count == 0,
            "the unloaded game-script types must not reappear");
        h.Check("after reload+rebuild, engine types are present",
            TypeCache.GetTypesDerivedFrom(typeof(Behaviour)).Count > 0);

        // ── Idempotence + cold-call safety ───────────────────────────────────────────────────────────
        // A second InvalidateAll (e.g. two reloads in a row, or a defensive double-call) must not throw and
        // must leave caches cold. Then a query in the cold window returns empty, never stale.
        ReloadCaches.InvalidateAll();
        ReloadCaches.InvalidateAll();
        h.Check("double InvalidateAll is safe + leaves TypeCache cold", !TypeCache.IsBuilt);
        h.Check("query while cold returns empty (no stale, no throw)",
            TypeCache.GetTypesDerivedFrom<ISample>().Count == 0);

        // Register is idempotent on the delegate: re-registering an already-listed invalidator must not grow
        // the list (a defensive double module-init can't double-clear).
        int before = ReloadCaches.RegisteredCount;
        ReloadCaches.Register(TypeCache.ClearForReload);
        h.Check("Register is idempotent on the same delegate",
            ReloadCaches.RegisteredCount == before,
            $"count changed {before} -> {ReloadCaches.RegisteredCount} on duplicate Register");

        // Restore the full build so later suites (and a re-run ordering) see a warm, complete universe.
        TypeCache.Build(engine, tests);

        return h.Report("Reload invalidation (P0.3)");
    }
}
