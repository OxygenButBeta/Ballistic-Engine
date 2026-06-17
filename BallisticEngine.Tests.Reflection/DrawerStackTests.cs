using System.Linq;
using System.Reflection;
using BallisticEngine;

namespace BallisticEngine.Tests.Reflection;

// B0 (editor-rework Phase B, Rule 1.5) contract + oracle: the Odin-style drawer stack's pure RESOLUTION half.
// The plan calls B0 the architectural core — it REPLACES the flat decorator chain (a fixed
// [Conditional,ReadOnly,HeaderSpace] list + single terminal) with a self-registering, deterministic,
// COMPOSABLE stack. The editor's runtime steps (the CallNext wrapping + ImGui) live in the host assembly and
// can't be referenced here, but the part that decides WHICH steps apply and IN WHAT ORDER is engine-side pure
// logic (DrawerStackResolver / DrawerStackPlan) and is the primary headless oracle for this chunk: no
// screenshot diff (ImGui AA is flaky), the resolve-plan-for-member test is the proof.
//
// Locks: (1) membership — a member carries exactly the steps its attributes warrant; (2) the COMPOSING case
// the chunk exists for — [ShowIf] + [ReadOnly] on ONE member yield BOTH steps, nested, not fighting; (3) the
// deterministic outer→inner order (Visibility → Chrome → Enable → terminal) by stage, independent of
// registration order (P0.4); (4) the kind split (Show/Hide → Visibility, Enable/Disable → Enable); (5) the
// per-member plan is cached (ARTIFACT-1, zero per-frame reflection) and drops on the central reload contract.
internal static class DrawerStackTests {
    static MemberInfo M(string name) =>
        typeof(DrawerStackSample).GetProperty(name)!;

    public static int Run() {
        var h = new Harness();

        Membership(h);
        Composition(h);
        OrderAndDeterminism(h);
        Caching(h);

        return h.Report("DrawerStack (B0)");
    }

    // Keys of the engine-known non-terminal steps (mirrors DrawerStackPlan.BuildDefault — if those change the
    // test must too, which is the point: the keys are the stable contract the editor binds its steps to).
    const string VisibilityKey = "BallisticEngine.Drawers.Conditional.Visibility";
    const string ChromeKey     = "BallisticEngine.Drawers.HeaderSpace";
    const string EnableKey     = "BallisticEngine.Drawers.Enable";

    static string[] StepKeys(MemberInfo member) =>
        DrawerStackPlan.Resolver.Resolve(member).Steps.Select(s => s.Key).ToArray();

    // ── Membership: each attribute → exactly its step; a bare member → no non-terminal steps ──────────────
    static void Membership(Harness h) {
        // The default engine resolver has NO terminal (the editor supplies it), so a bare member resolves to
        // an EMPTY stack — the model knows how to ORDER, not how to DRAW.
        h.CheckStrings("plain member → no steps", StepKeys(M(nameof(DrawerStackSample.Plain))));

        h.CheckStrings("[ReadOnly] → one Enable step",
            StepKeys(M(nameof(DrawerStackSample.ReadOnlyOnly))), EnableKey);

        h.CheckStrings("[ShowIf] → one Visibility step",
            StepKeys(M(nameof(DrawerStackSample.VisibleIf))), VisibilityKey);

        h.CheckStrings("[Header] → one Chrome step",
            StepKeys(M(nameof(DrawerStackSample.Headed))), ChromeKey);

        h.CheckStrings("[Space] → one Chrome step",
            StepKeys(M(nameof(DrawerStackSample.Spaced))), ChromeKey);

        // Kind split: [DisableIf] is an ENABLE-stage condition, NOT visibility.
        h.CheckStrings("[DisableIf] → Enable step (not Visibility)",
            StepKeys(M(nameof(DrawerStackSample.DisabledIf))), EnableKey);
    }

    // ── The composing case: the reason B0 exists — [ShowIf] + [Header] + [ReadOnly] on ONE member ──────────
    static void Composition(Harness h) {
        string[] keys = StepKeys(M(nameof(DrawerStackSample.Combo)));

        // All three steps present — they COMPOSE, they don't overwrite each other (the flat-list failure).
        h.Check("combo has Visibility step", keys.Contains(VisibilityKey));
        h.Check("combo has Chrome step", keys.Contains(ChromeKey));
        h.Check("combo has Enable step", keys.Contains(EnableKey));
        h.Check("combo has exactly 3 non-terminal steps", keys.Length == 3);

        // The OUTER→INNER order is deterministic by stage: Visibility wraps Chrome wraps Enable. This is the
        // structural guarantee the flat chain couldn't make (its order was a hand-written list).
        h.CheckStrings("combo order is Visibility → Chrome → Enable", keys,
            VisibilityKey, ChromeKey, EnableKey);
    }

    // ── Determinism (P0.4): order is by stage/priority/key, NOT registration order ─────────────────────────
    static void OrderAndDeterminism(Harness h) {
        // Build TWO resolvers registering the SAME descriptors in DIFFERENT order; the resolved stack for the
        // combo member must be IDENTICAL — order is a total function of the set, not of load order.
        var forward = new DrawerStackResolver();
        forward.Register(MakeVisibility());
        forward.Register(MakeChrome());
        forward.Register(MakeEnable());

        var reverse = new DrawerStackResolver();
        reverse.Register(MakeEnable());
        reverse.Register(MakeChrome());
        reverse.Register(MakeVisibility());

        MemberInfo combo = M(nameof(DrawerStackSample.Combo));
        string[] f = forward.Resolve(combo).Steps.Select(s => s.Key).ToArray();
        string[] r = reverse.Resolve(combo).Steps.Select(s => s.Key).ToArray();
        h.CheckStrings("forward registration order", f, VisibilityKey, ChromeKey, EnableKey);
        h.CheckStrings("reverse registration → SAME order", r, VisibilityKey, ChromeKey, EnableKey);

        // Two descriptors at the SAME stage break ties by ordinal Key (not registration). Register two Enable
        // descriptors out of alphabetical order; the lower key sorts first.
        var tie = new DrawerStackResolver();
        tie.Register(new DrawerStackResolver.Descriptor {
            Key = "zzz.enable", Stage = DrawerStage.Enable, Priority = 0,
            Applies = m => m.GetCustomAttribute<ReadOnlyAttribute>() is not null,
        });
        tie.Register(new DrawerStackResolver.Descriptor {
            Key = "aaa.enable", Stage = DrawerStage.Enable, Priority = 0,
            Applies = m => m.GetCustomAttribute<ReadOnlyAttribute>() is not null,
        });
        string[] tied = tie.Resolve(M(nameof(DrawerStackSample.ReadOnlyOnly))).Steps.Select(s => s.Key).ToArray();
        h.CheckStrings("equal-stage ties break by ordinal key (not load order)", tied, "aaa.enable", "zzz.enable");

        // Priority overrides key within a stage: a higher-priority step sorts OUTER even with a later key.
        var prio = new DrawerStackResolver();
        prio.Register(new DrawerStackResolver.Descriptor {
            Key = "aaa.enable", Stage = DrawerStage.Enable, Priority = 0,
            Applies = m => m.GetCustomAttribute<ReadOnlyAttribute>() is not null,
        });
        prio.Register(new DrawerStackResolver.Descriptor {
            Key = "zzz.enable", Stage = DrawerStage.Enable, Priority = 10,   // higher → outer
            Applies = m => m.GetCustomAttribute<ReadOnlyAttribute>() is not null,
        });
        string[] byPrio = prio.Resolve(M(nameof(DrawerStackSample.ReadOnlyOnly))).Steps.Select(s => s.Key).ToArray();
        h.CheckStrings("higher priority sorts outer within a stage", byPrio, "zzz.enable", "aaa.enable");

        // A single deterministic TERMINAL is appended last; the highest-priority applicable terminal wins.
        var withTerminal = new DrawerStackResolver();
        withTerminal.Register(MakeEnable());
        withTerminal.Register(new DrawerStackResolver.Descriptor {
            Key = "term.low", Stage = DrawerStage.Terminal, Priority = 0, Applies = _ => true,
        });
        withTerminal.Register(new DrawerStackResolver.Descriptor {
            Key = "term.high", Stage = DrawerStage.Terminal, Priority = 5, Applies = _ => true,
        });
        var stack = withTerminal.Resolve(M(nameof(DrawerStackSample.ReadOnlyOnly)));
        h.Check("terminal is the single leaf (last step)", stack.HasTerminal && stack.Terminal.Key == "term.high");
        h.Check("only ONE terminal kept", stack.Steps.Count(s => s.IsTerminal) == 1);
        h.CheckStrings("terminal appended after non-terminal steps",
            stack.Steps.Select(s => s.Key), EnableKey, "term.high");
    }

    // ── Caching (ARTIFACT-1) + reload invalidation ────────────────────────────────────────────────────────
    static void Caching(Harness h) {
        DrawerStackPlan.Clear();
        MemberInfo combo = M(nameof(DrawerStackSample.Combo));
        var first = DrawerStackPlan.For(combo);
        var second = DrawerStackPlan.For(combo);
        h.Check("member plan is cached (same instance)", ReferenceEquals(first, second));

        // The central reload contract drops the cache (so a stale plan against an old ALC type is never
        // served). After ReloadCaches.InvalidateAll the next For() recomputes a fresh (but equal) plan.
        ReloadCaches.InvalidateAll();
        var afterReload = DrawerStackPlan.For(combo);
        h.Check("plan recomputed after reload invalidation", !ReferenceEquals(first, afterReload));
        h.CheckStrings("recomputed plan is equivalent",
            afterReload.Steps.Select(s => s.Key), VisibilityKey, ChromeKey, EnableKey);

        // InvalidateAll above also cleared TypeCache (the central contract drains EVERY cache). Restore the
        // full build so this suite leaves the shared state warm for any later suite + re-run ordering.
        TypeCache.Build(typeof(ComponentRegistry).Assembly, typeof(DrawerStackTests).Assembly);
    }

    // Local descriptor factories mirroring DrawerStackPlan.BuildDefault (so the ordering test owns its inputs).
    static DrawerStackResolver.Descriptor MakeVisibility() => new() {
        Key = VisibilityKey, Stage = DrawerStage.Visibility, Priority = 0,
        Applies = m => m.GetCustomAttributes<ConditionalAttribute>().Any(c => c.Kind is ConditionKind.Show or ConditionKind.Hide),
    };
    static DrawerStackResolver.Descriptor MakeChrome() => new() {
        Key = ChromeKey, Stage = DrawerStage.Chrome, Priority = 0,
        Applies = m => m.GetCustomAttribute<HeaderAttribute>() is not null || m.GetCustomAttribute<SpaceAttribute>() is not null,
    };
    static DrawerStackResolver.Descriptor MakeEnable() => new() {
        Key = EnableKey, Stage = DrawerStage.Enable, Priority = 0,
        Applies = m => m.GetCustomAttribute<ReadOnlyAttribute>() is not null
                    || m.GetCustomAttributes<ConditionalAttribute>().Any(c => c.Kind is ConditionKind.Enable or ConditionKind.Disable),
    };
}
