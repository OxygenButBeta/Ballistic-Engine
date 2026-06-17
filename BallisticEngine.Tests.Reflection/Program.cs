using System.Reflection;
using BallisticEngine;
using BallisticEngine.Tests.Reflection;

// Phase-0 headless harness entry point. Builds TypeCache over the engine + this test assembly, then
// runs every P0 suite. Exit code = total failing checks across all suites (0 = green).

int exit = 0;
exit += TypeCacheTests.Run();
exit += PropertyModelTests.Run();
exit += ReloadInvalidationTests.Run();
exit += MenuRegistryTests.Run();
exit += OrderedPassListTests.Run();
exit += InputActionChainTests.Run();
exit += DrawerStackTests.Run();
exit += ComponentPreviewTests.Run();
exit += AssetInspectorTests.Run();
exit += EntityRefTests.Run();        // G1 — inserts a scene (no public unload); runs before G0's own
exit += CollectionTests.Run();       // G2 — inserts a scene (no public unload); runs before G0's own
exit += SerializeReferenceTests.Run(); // G3 -- inserts a scene (no public unload); runs before G0's own
exit += NestedTests.Run();             // G4 -- inserts a scene (no public unload); runs before G0's own
exit += SerializerDropTests.Run();   // G0 — runs LAST (inserts a scene with no public unload)
return exit;

internal static class TypeCacheTests {
    public static int Run() {
        var h = new Harness();

        // Build over the engine assembly AND this test assembly (so the sample fixtures are in the
        // scanned universe), exactly as EngineBootstrap builds over engine + host + game scripts.
        Assembly engine = typeof(ComponentRegistry).Assembly;
        Assembly tests = typeof(TypeCacheTests).Assembly;
        TypeCache.Build(engine, tests);

        h.Check("IsBuilt after Build", TypeCache.IsBuilt);

        // ── GetTypesDerivedFrom: the core concrete+closed-generic+instantiable filter ──────────────
        // From ISample's universe: only the instantiable concrete implementors. Excluded: the interface
        // itself, the abstract implementor, the no-default-ctor one, the private-ctor one, the OPEN
        // generic; INCLUDED: the closed-generic and the concrete subclass of the abstract one.
        h.CheckSet("derived ISample = concrete instantiable only",
            TypeCache.GetTypesDerivedFrom<ISample>(),
            typeof(SampleA), typeof(SampleB), typeof(SampleConcreteSub), typeof(SampleClosedGeneric));

        // Per-exclusion spelled out so a regression points at the exact rule it broke.
        var iSample = TypeCache.GetTypesDerivedFrom<ISample>();
        h.Check("interface excluded from its own query", !iSample.Contains(typeof(ISample)));
        h.Check("abstract implementor excluded", !iSample.Contains(typeof(SampleAbstract)));
        h.Check("no-default-ctor excluded", !iSample.Contains(typeof(SampleNoDefaultCtor)));
        h.Check("private-ctor excluded", !iSample.Contains(typeof(SamplePrivateCtor)));
        h.Check("open generic excluded", !iSample.Contains(typeof(SampleOpenGeneric<>)));
        h.Check("closed generic included", iSample.Contains(typeof(SampleClosedGeneric)));
        h.Check("concrete subclass of abstract included", iSample.Contains(typeof(SampleConcreteSub)));

        // Abstract BASE used as query type: excluded from its own query, subclass included.
        h.CheckSet("derived abstract-base = subclass only",
            TypeCache.GetTypesDerivedFrom<SampleAbstractBase>(),
            typeof(SampleAbstractBaseSub));

        // Concrete BASE used as query type: the instantiable base IS included alongside its subclass
        // (IsAssignableFrom is reflexive; the base is concrete+instantiable).
        h.CheckSet("derived concrete-base = base + subclass",
            TypeCache.GetTypesDerivedFrom<SampleConcreteBase>(),
            typeof(SampleConcreteBase), typeof(SampleConcreteBaseSub));

        // Determinism (P0.4 substrate): the SAME query returns a stable, ordinal-by-FullName order.
        var ordered1 = TypeCache.GetTypesDerivedFrom<ISample>().ToArray();
        var ordered2 = TypeCache.GetTypesDerivedFrom<ISample>().OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();
        h.CheckSequence("derived order is ordinal-by-FullName (deterministic)", ordered1, ordered2);

        // ── GetTypesWithAttribute: concrete-only ──────────────────────────────────────────────────
        h.CheckSet("types-with-marker = concrete only",
            TypeCache.GetTypesWithAttribute<SampleMarkerAttribute>(),
            typeof(SampleMarked));
        h.Check("abstract marked type excluded",
            !TypeCache.GetTypesWithAttribute<SampleMarkerAttribute>().Contains(typeof(SampleMarkedAbstract)));

        // ── GetMethodsWithAttribute: public STATIC only (the [MenuItem] shape) ─────────────────────
        var marked = TypeCache.GetMethodsWithAttribute<SampleMarkerAttribute>();
        var markedNames = marked.Select(m => m.Name).ToHashSet();
        h.Check("marked static method found", markedNames.Contains(nameof(SampleMenuHost.MarkedStatic)));
        h.Check("unmarked static method absent", !markedNames.Contains(nameof(SampleMenuHost.UnmarkedStatic)));
        h.Check("marked INSTANCE method absent (static-only)", !markedNames.Contains(nameof(SampleMenuHost.MarkedInstance)));

        // ── Engine sanity: TypeCache must agree with ComponentRegistry's own scan ─────────────────
        // Every type ComponentRegistry put in its component menu must also be a concrete instantiable
        // Behaviour per TypeCache — proves TypeCache generalizes (not contradicts) the existing scan.
        ComponentRegistry.Build(engine);
        var behaviours = TypeCache.GetTypesDerivedFrom(typeof(Behaviour)).ToHashSet();
        h.Check("TypeCache found Behaviour subtypes", behaviours.Count > 0,
            $"expected >0 concrete Behaviour types, got {behaviours.Count}");
        bool registryAgreement = ComponentRegistry.Menu.All(e => behaviours.Contains(e.Type));
        h.Check("every ComponentRegistry menu type is a TypeCache Behaviour", registryAgreement);

        // ── Rebuild semantics: a fresh Build() must drop types no longer scanned (hot-reload contract).
        // Rebuild over the ENGINE ONLY (test assembly excluded) → the sample fixtures must vanish.
        TypeCache.Build(engine);
        h.Check("rebuild drops unscanned (game-script) types",
            TypeCache.GetTypesDerivedFrom<ISample>().Count == 0,
            "sample fixtures should be gone after rebuilding without the test assembly");
        // Behaviour types persist (still in the engine assembly) — rebuild didn't nuke everything.
        h.Check("rebuild keeps engine types",
            TypeCache.GetTypesDerivedFrom(typeof(Behaviour)).Count > 0);

        // Restore the full build so subsequent suites (future chunks) see the fixtures again.
        TypeCache.Build(engine, tests);

        return h.Report("TypeCache (P0.1)");
    }
}
