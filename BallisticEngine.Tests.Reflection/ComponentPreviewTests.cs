using System.Reflection;
using BallisticEngine;

namespace BallisticEngine.Tests.Reflection;

// B1 (editor-rework Phase B, Rule 1) component-preview registry contract + oracle, tested HEADLESSLY at the
// substrate level. B1 deletes the `if (behaviour is Renderer/Volume/Terrain/...) DrawXxxSection(...)` god-chain
// in InspectorPanel and replaces it with a self-registering, type-resolved preview registry — exactly the
// EditorWindowRegistry (A1) pattern applied to component sections. The editor's ComponentPreviewRegistry +
// IComponentPreview live in the host assembly and can't be referenced here, so (like MenuRegistryTests for A1)
// this suite proves the engine-side pieces the registry is built on — TypeCache discovery of
// [ComponentPreview], the assignable-by-TargetType match, and DeterministicResolver ordering — and that, fed
// the SAME inputs with the SAME real primitives, they yield the exact preview set the editor would draw. If
// these pass, the registry's only remaining job (Activator.CreateInstance + delegating into InspectorPanel's
// internal DrawXxxSection methods) is trivial glue.
//
// Covered: (1) the [ComponentPreview] attribute round-trips TargetType/Priority; (2) discovery finds the
// fixture previews; (3) membership — a component type resolves to exactly its applicable previews, a bare
// component to NONE (so it just draws members, the safety net); (4) base-type preview COVERS a subclass
// (assignable match, the `behaviour is Renderer` semantics); (5) deterministic order — priority desc then
// type name, independent of registration order (P0.4); (6) AllowMultiple → one entry per target; (7) the
// rebuild-over-engine-only hot-reload substrate the registry's ReloadCaches callback rides on.
internal static class ComponentPreviewTests {
    // A mirror of one resolved entry (the editor Entry is unreferenceable). Discovered the SAME way the editor
    // registry discovers it, so an ordering/membership regression in the shared primitives surfaces here.
    readonly record struct PreviewEntry(Type TargetType, int Priority, string TieKey, Type PreviewType);

    // Re-runs the registry's discovery over the fixture previews using the real engine primitives — the exact
    // shape of ComponentPreviewRegistry.Discover (minus the Activator.CreateInstance the editor does).
    static List<PreviewEntry> Discover() {
        var entries = new List<PreviewEntry>();
        foreach (Type type in TypeCache.GetTypesWithAttribute<ComponentPreviewAttribute>()) {
            // Editor side filters to IComponentPreview; here the stand-in marker is ISamplePreview. Keep only
            // the fixtures so an unrelated [ComponentPreview] elsewhere can't perturb the counts.
            if (!typeof(ISamplePreview).IsAssignableFrom(type))
                continue;
            foreach (ComponentPreviewAttribute attr in type.GetCustomAttributes<ComponentPreviewAttribute>()) {
                if (attr.TargetType is null) continue;
                string tieKey = $"{attr.TargetType.FullName} {type.FullName}";
                entries.Add(new PreviewEntry(attr.TargetType, attr.Priority, tieKey, type));
            }
        }
        return entries;
    }

    // The resolution PreviewsFor performs: the previews whose TargetType is assignable from componentType, in
    // deterministic draw order (priority desc, then ordinal tie-key) via the real DeterministicResolver.
    static List<Type> PreviewsFor(List<PreviewEntry> all, Type componentType) {
        var resolver = new DeterministicResolver<Type>();
        foreach (PreviewEntry e in all)
            if (e.TargetType.IsAssignableFrom(componentType))
                resolver.Register(e.PreviewType, priority: e.Priority, tieKey: e.TieKey);
        return resolver.All().ToList();
    }

    public static int Run() {
        var h = new Harness();

        Assembly engine = typeof(ComponentRegistry).Assembly;
        Assembly tests = typeof(ComponentPreviewTests).Assembly;
        TypeCache.Build(engine, tests);

        // ── (1) Attribute round-trip ──────────────────────────────────────────────────────────────────────
        var highAttr = typeof(SampleComponentPreviewHigh).GetCustomAttribute<ComponentPreviewAttribute>();
        h.Check("[ComponentPreview] TargetType round-trips", highAttr is { TargetType: not null }
            && highAttr.TargetType == typeof(SamplePreviewComponent));
        h.Check("[ComponentPreview] Priority round-trips", highAttr is { Priority: 10 });

        var defaultAttr = typeof(SampleComponentPreview).GetCustomAttribute<ComponentPreviewAttribute>();
        h.Check("[ComponentPreview] default Priority is 0", defaultAttr is { Priority: 0 });

        // ── (2) Discovery finds the fixtures via TypeCache ──────────────────────────────────────────────────
        List<PreviewEntry> all = Discover();
        var previewTypes = all.Select(e => e.PreviewType).ToHashSet();
        h.Check("discovery finds the component preview", previewTypes.Contains(typeof(SampleComponentPreview)));
        h.Check("discovery finds the high-priority preview", previewTypes.Contains(typeof(SampleComponentPreviewHigh)));
        h.Check("discovery finds the multi-target preview", previewTypes.Contains(typeof(SampleMultiTargetPreview)));

        // ── (3) Membership: a component resolves to its previews; a bare component to NONE ──────────────────
        var forComponent = PreviewsFor(all, typeof(SamplePreviewComponent));
        h.Check("component resolves to its 3 previews", forComponent.Count == 3,
            $"expected 3, got {forComponent.Count}: [{string.Join(", ", forComponent.Select(t => t.Name))}]");

        var forBare = PreviewsFor(all, typeof(SamplePreviewBareComponent));
        h.Check("bare component resolves to NO previews (member-only fallback)", forBare.Count == 0,
            $"expected 0, got [{string.Join(", ", forBare.Select(t => t.Name))}]");

        // ── (4) Base-type preview COVERS a subclass (the `behaviour is Renderer` assignable semantics) ─────
        var forSub = PreviewsFor(all, typeof(SamplePreviewSubComponent));
        // The subclass gets BOTH the base-typed previews (3) AND the multi-target preview registered directly
        // for the subclass (1) = 4.
        h.Check("subclass inherits base-typed previews", forSub.Contains(typeof(SampleComponentPreview)));
        h.Check("subclass also gets its directly-registered preview", forSub.Contains(typeof(SampleMultiTargetPreview)));
        h.Check("subclass total = base previews + own", forSub.Count == 4,
            $"expected 4, got [{string.Join(", ", forSub.Select(t => t.Name))}]");

        // ── (5) Deterministic order: priority desc, then ordinal tie-key (NOT registration order) ──────────
        // High (priority 10) first; the two default-priority previews then by type full name (C before S).
        h.CheckStrings("preview order = priority desc then type name",
            forComponent.Select(t => t.Name),
            nameof(SampleComponentPreviewHigh), nameof(SampleComponentPreview), nameof(SampleComponentPreviewSecond));

        // Independent of registration order: resolving from a reversed entry list yields the SAME order.
        var reversed = Enumerable.Reverse(all).ToList();
        var fromReversed = PreviewsFor(reversed, typeof(SamplePreviewComponent));
        h.CheckStrings("order is independent of registration order", fromReversed.Select(t => t.Name),
            nameof(SampleComponentPreviewHigh), nameof(SampleComponentPreview), nameof(SampleComponentPreviewSecond));

        // ── (6) AllowMultiple → one entry per target ────────────────────────────────────────────────────────
        int multiEntries = all.Count(e => e.PreviewType == typeof(SampleMultiTargetPreview));
        h.Check("AllowMultiple → one entry per [ComponentPreview]", multiEntries == 2);
        var forOther = PreviewsFor(all, typeof(SamplePreviewOtherComponent));
        h.CheckStrings("multi-target preview applies to its other target", forOther.Select(t => t.Name),
            nameof(SampleMultiTargetPreview));

        // ── (7) Hot-reload substrate: rebuild over engine-only drops the fixture previews ───────────────────
        // The editor registry's ReloadCaches callback re-discovers over the rebuilt TypeCache; here we prove the
        // underlying query goes empty for the (now-unscanned) fixtures, so a re-discovery would too.
        TypeCache.Build(engine);
        bool fixturesGone = TypeCache.GetTypesWithAttribute<ComponentPreviewAttribute>()
            .All(t => !typeof(ISamplePreview).IsAssignableFrom(t));
        h.Check("rebuild over engine-only drops fixture [ComponentPreview]s", fixturesGone);

        // Restore the full build so later suites (and re-runs) see the fixtures again.
        TypeCache.Build(engine, tests);

        return h.Report("ComponentPreview registry (B1)");
    }
}
