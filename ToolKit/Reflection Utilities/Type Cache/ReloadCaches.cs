namespace BallisticEngine;

// The P0.3 hot-reload invalidation contract (editor-rework plan, Phase 0). ONE central list of "drop
// your stale reflection" callbacks so a game-script hot-reload never serves a cache built over the old,
// now-unloaded ALC.
//
// WHY this exists (the bug it prevents): reflection caches like TypeCache (Type[] per query) and TypePlan
// (a compiled member plan per Type — MemberInfo handles into the game-script assembly) memoize results
// keyed by, and POINTING AT, types from the collectible script ALC. When ReloadGameScripts unloads that
// ALC and loads a new one, those cached entries reference the OLD types: a TypePlan built for the old
// `Foo` would still be served for the new `Foo`, and a TypeCache derived-type list would still contain the
// stale type objects. The symptom is the classic "first hot-reload breaks" bug the InputRegistry /
// NetworkReplicationRegistry / SceneReplicationRegistry scar-comments already warn about — those three drop
// ALC-PINNING handles (so the ALC can unload); these drop STALE reflection (so queries stay correct). Both
// must clear at the same reload boundary.
//
// THE ONE-LINE RULE (§5.6 / P0.3): every new reflection cache (TypeCache, TypePlan, and the future window /
// command / drawer-plan caches A1/B0/D1 will add) self-registers its invalidation here ONCE — typically via
// a [ModuleInitializer] so registration is guaranteed at assembly load, not lazily on first static touch.
// EngineBootstrap.ReloadGameScripts then calls InvalidateAll() once; no cache is ever hand-listed at the
// reload site again. Adding a cache = one Register() call next to it; the reload path is never edited.
//
// Lives in the ENGINE (alongside TypeCache) so the headless serializer / runtime — which also hot-reload —
// share the same contract; it is not editor-only.
public static class ReloadCaches {
    // Each entry drops one cache's stale entries. Insertion order is preserved but invalidation is
    // order-independent by design (each callback only clears its OWN cache; none depends on another's
    // state), so registration race between module initializers is harmless.
    static readonly List<Action> invalidators = new();

    // Register a cache's "clear everything" callback. Idempotent on the delegate so a double module-init
    // (defensive) can't double-list the same clear. Call this once per cache — conventionally from a
    // [ModuleInitializer] right beside the cache, so it is wired before any reload can occur.
    public static void Register(Action invalidate) {
        if (invalidate is null || invalidators.Contains(invalidate))
            return;
        invalidators.Add(invalidate);
    }

    // Drop every registered cache. Called from EngineBootstrap.ReloadGameScripts at the same boundary as
    // the InputRegistry / NetworkReplicationRegistry / SceneReplicationRegistry ClearForReload calls, BEFORE
    // GameScripts.Unload — so no cached MemberInfo/Type from the old ALC survives into the new assembly's
    // queries. The caches lazily rebuild on the next ask over the freshly-built TypeCache.
    public static void InvalidateAll() {
        foreach (Action invalidate in invalidators)
            invalidate();
    }

    // Test/diagnostic visibility: how many caches are wired into the contract. The harness asserts this is
    // non-zero (proves the [ModuleInitializer]s ran) and grows as chunks add caches.
    public static int RegisteredCount => invalidators.Count;
}
