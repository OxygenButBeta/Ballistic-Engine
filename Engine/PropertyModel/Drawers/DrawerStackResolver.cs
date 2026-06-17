using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace BallisticEngine;

// editor-rework B0 (Phase B, Rule 1.5) — the PURE, headless RESOLUTION half of the Odin-style drawer stack.
// This is the architectural core the plan calls out: it REPLACES the flat decorator chain's hardcoded
// `[Conditional, ReadOnly, HeaderSpace]` list + single terminal (DrawerPipeline.CreateDefault) with a
// self-registering, deterministic, attribute-keyed STACK. The actual ImGui drawing lives editor-side (each
// descriptor's runtime step wraps the next via CallNext); the part that decides WHICH steps apply and IN
// WHAT ORDER is pure logic — so it lives in the engine and is harness-tested with NO ImGui (the §4
// "zero per-frame reflection" rule is satisfied because the resolved plan is cached by member, ARTIFACT-1 style).
//
// Why pure resolution matters: with self-registration across assemblies, "which drawer wins / in what order"
// must be a total function of the registered SET, never registration (assembly-load) order — the exact P0.4
// bug. This resolver leans on DeterministicResolver for that guarantee and exposes the resolved stack so the
// harness can lock it (HideIf is outermost, terminal is the single leaf, equal-stage ties break by key, the
// SAME plan on every machine). The editor binds each descriptor to a concrete runtime step; the editor's
// stack-build asks THIS resolver which descriptors apply, so the algorithm under test IS the one drawn.
//
// Membership is decided on a MemberInfo (its attributes), NOT a TypePlan.Member — so BOTH inspector hosts key
// off the same thing: the component path uses the reflected member, the volume path uses the parameter's
// backing field (slot.Field). Both carry the cross-cutting attributes ([Header]/[ReadOnly]/[ShowIf]); the
// resolver reads only those, never the value, so the order is shared and the two paths can't drift.
public sealed class DrawerStackResolver {
    // One registered drawer kind. `Stage` fixes its outer→inner band; `Priority` (higher = more outer within
    // a stage) + `Key` (stable ordinal tie-break) make the order deterministic. `Applies` decides membership
    // for a given member by its attributes — a pure predicate over the MemberInfo, never the live value
    // (live-value gating, e.g. [HideIf]'s truthiness, happens at DRAW time inside the runtime step; this only
    // decides the member CARRIES the step). The Terminal stage's Applies typically tests the value TYPE.
    public sealed class Descriptor {
        public required string Key { get; init; }              // stable id + ordinal tie-break (drawer type name)
        public required DrawerStage Stage { get; init; }
        public required int Priority { get; init; }            // within a stage: higher sorts OUTER (drawn first)
        public required Func<MemberInfo, bool> Applies { get; init; }
        public bool IsTerminal => Stage == DrawerStage.Terminal;
    }

    readonly List<Descriptor> descriptors = new();

    // A resolved plan for ONE member: the ordered stack (outer→inner, terminal last). Cached by member in
    // DrawerStackPlan; never recomputed per frame.
    public sealed class MemberStack {
        public required MemberInfo Member { get; init; }
        public required IReadOnlyList<Descriptor> Steps { get; init; }   // outer→inner; last is the terminal
        public Descriptor Terminal => Steps.Count > 0 && Steps[^1].IsTerminal ? Steps[^1] : null;
        public bool HasTerminal => Terminal is not null;
    }

    public int Count => descriptors.Count;

    public void Register(Descriptor d) => descriptors.Add(d);

    // Convenience for the common "this attribute type, present on the member, puts a step at this stage" case.
    public void RegisterAttribute<TAttr>(DrawerStage stage, int priority = 0, string key = null)
        where TAttr : Attribute =>
        Register(new Descriptor {
            Key = key ?? typeof(TAttr).FullName,
            Stage = stage,
            Priority = priority,
            Applies = m => m.GetCustomAttribute<TAttr>() is not null,
        });

    // Resolve the ordered stack for a member. Membership = every descriptor whose `Applies` is true. Order =
    // by Stage ascending (Visibility outermost → Terminal leaf), then within a stage by the SAME deterministic
    // rule as every other self-registering registry (priority desc, then ordinal key asc — via
    // DeterministicResolver), so the result is independent of registration order. AT MOST ONE terminal is
    // kept (the deterministic winner among applicable terminals — a custom type drawer can out-priority a
    // built-in for the same type, mirroring DrawerRegistry's override intent but made deterministic).
    public MemberStack Resolve(MemberInfo member) {
        var nonTerminal = new List<Descriptor>();
        var terminals = new DeterministicResolver<Descriptor>();

        foreach (Descriptor d in descriptors) {
            if (!d.Applies(member)) continue;
            if (d.IsTerminal) terminals.Register(d, d.Priority, d.Key);
            else nonTerminal.Add(d);
        }

        // Non-terminal steps: stage ascending (outer→inner), then deterministic within stage.
        IEnumerable<Descriptor> ordered = nonTerminal
            .OrderBy(d => (int)d.Stage)
            .ThenByDescending(d => d.Priority)
            .ThenBy(d => d.Key, StringComparer.Ordinal);

        var steps = ordered.ToList();

        // The single deterministic terminal (if any) is appended as the leaf.
        Descriptor terminal = terminals.Resolve(_ => true);
        if (terminal is not null)
            steps.Add(terminal);

        return new MemberStack { Member = member, Steps = steps };
    }
}

// ARTIFACT-1-style STATIC cache of the resolved member stacks, keyed by MemberInfo so the per-frame draw path
// NEVER re-resolves (the §4 perf rule — like TypePlan, reflection runs once at first ask). Drops on hot-reload
// via the central ReloadCaches contract (P0.3) so a stack built against the old game-script ALC's `Foo` is
// never served for the new `Foo`. The editor's runtime stack builder reads these cached plans.
public static class DrawerStackPlan {
    // The resolver the whole inspector shares. The editor registers its concrete drawer descriptors into this
    // ONE instance at startup (self-registration); the model ships it pre-loaded with the engine-known
    // attribute steps so the headless harness can resolve a stack with no editor present. Editor terminals
    // (the type drawers) register on top; without them a member resolves to a no-terminal stack (the model
    // doesn't know how to DRAW, only how to ORDER).
    public static DrawerStackResolver Resolver { get; private set; } = BuildDefault();

    static readonly Dictionary<MemberInfo, DrawerStackResolver.MemberStack> cache = new();

    public static DrawerStackResolver.MemberStack For(MemberInfo member) {
        if (cache.TryGetValue(member, out DrawerStackResolver.MemberStack cached))
            return cached;
        DrawerStackResolver.MemberStack stack = Resolver.Resolve(member);
        cache[member] = stack;
        return stack;
    }

    // Replace the shared resolver (the editor calls this once at startup with its terminals added) and drop
    // any plans resolved against the previous resolver.
    public static void SetResolver(DrawerStackResolver resolver) {
        Resolver = resolver ?? BuildDefault();
        cache.Clear();
    }

    // Drop every cached member stack (hot-reload + harness). The resolver registrations themselves are
    // assembly-static; only the per-member resolved plans are invalidated.
    public static void Clear() => cache.Clear();

    [ModuleInitializer]
    internal static void RegisterReloadInvalidation() => ReloadCaches.Register(Clear);

    // The engine-known, ImGui-free stack steps: the cross-cutting attribute decorators the old flat list
    // hardcoded, now each a self-registered descriptor at its stage. The EDITOR adds the Terminal type
    // drawers + any custom attribute drawers on top. Order WITHIN a stage is by priority then key — listed
    // here only for registration; the resolver re-derives the deterministic order.
    public static DrawerStackResolver BuildDefault() {
        var r = new DrawerStackResolver();

        // Visibility (outermost): [ShowIf]/[HideIf]. One descriptor matches any ConditionalAttribute carrying
        // a Show/Hide kind (a member can have several; the runtime step ANDs them, as Conditions.Visible does).
        r.Register(new DrawerStackResolver.Descriptor {
            Key = "BallisticEngine.Drawers.Conditional.Visibility",
            Stage = DrawerStage.Visibility,
            Priority = 0,
            Applies = HasVisibilityCondition,
        });

        // Chrome: [Header]/[Space] above the row.
        r.Register(new DrawerStackResolver.Descriptor {
            Key = "BallisticEngine.Drawers.HeaderSpace",
            Stage = DrawerStage.Chrome,
            Priority = 0,
            Applies = m => m.GetCustomAttribute<HeaderAttribute>() is not null
                        || m.GetCustomAttribute<SpaceAttribute>() is not null,
        });

        // Enable: [ReadOnly] OR an [EnableIf]/[DisableIf] condition. (ReadOnly and the conditional disable
        // collapse into ONE enable step — the runtime step ORs them, as DrawMember's `attrs.ReadOnly ||
        // Conditions.Disabled` does — so they can't fight over order.)
        r.Register(new DrawerStackResolver.Descriptor {
            Key = "BallisticEngine.Drawers.Enable",
            Stage = DrawerStage.Enable,
            Priority = 0,
            Applies = m => m.GetCustomAttribute<ReadOnlyAttribute>() is not null
                        || HasEnableCondition(m),
        });

        return r;
    }

    static bool HasVisibilityCondition(MemberInfo m) {
        foreach (ConditionalAttribute c in m.GetCustomAttributes<ConditionalAttribute>())
            if (c.Kind is ConditionKind.Show or ConditionKind.Hide) return true;
        return false;
    }

    static bool HasEnableCondition(MemberInfo m) {
        foreach (ConditionalAttribute c in m.GetCustomAttributes<ConditionalAttribute>())
            if (c.Kind is ConditionKind.Enable or ConditionKind.Disable) return true;
        return false;
    }
}
