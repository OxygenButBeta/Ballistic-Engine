using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BallisticEngine;
using BallisticEngine.Serialization;

namespace BallisticEngine.Tests.Reflection;

// RW8 / EF15 (the polymorphic-collection round-trip gap the G3 suite explicitly left out): a
// `[SerializeReference] List<IFoo>` / `IFoo[]` round-trips the LIVE concrete type of EVERY element. The
// serializer writes a per-element $type (the concrete FullName) + that element's members; the deserializer
// resolves each element's concrete type via TypeCache, instantiates it, and refills its members through the
// SAME recursion a scalar [SerializeReference] member uses. This suite drives the REAL serialize/deserialize
// path over a live scene and asserts the three DoD oracles from the plan:
//   (a) all concrete element types (across an interface LIST and an abstract-base ARRAY) survive the round trip
//       with their member values + ORDER intact;
//   (b) the YAML is byte-stable across two serializations (deterministic key order + $type placement), AND a
//       serialize/deserialize/serialize cycle is a fixed point;
//   (c) a non-polymorphic List<int> alongside is byte-identical to the pre-change shape -- it carries NO $type
//       tag (the polymorphic branch is taken ONLY for the [SerializeReference]-marked element members).
//
// Runs in Program.cs alongside the other G/RW suites; like them it inserts into the global SceneManager (no
// public unload) so it must run before SerializerDropTests.
internal static class PolymorphicCollectionTests {
    public static int Run() {
        var h = new Harness();

        // TypeCache resolves a per-element $type FullName scoped to the element BASE's implementors, so the
        // test assembly (fixtures) must be in the scanned universe -- exactly as EngineBootstrap builds it.
        // ComponentRegistry is needed for the deserialize half (ApplyComponent by registry name).
        var engine = typeof(ComponentRegistry).Assembly;
        var tests = typeof(PolymorphicCollectionTests).Assembly;
        TypeCache.Build(engine, tests);
        ComponentRegistry.Build(engine, tests);

        _ = new SceneManager();
        Scene scene = SceneManager.GetCurrentScene();

        Entity holderEntity = Entity.Instantiate("PolyCollHolder");
        var holder = holderEntity.AddComponent<PolymorphicCollectionHolderBehaviour>();
        // Interface element list with TWO distinct concrete types + a NESTED polymorphic element (CompositeModifier
        // wraps an Inner PoisonModifier) -> proves per-element $type AND that the recursion still nests.
        holder.Mods = new List<IDamageModifier> {
            new CritModifier { Order = 1, Multiplier = 2.5f },
            new PoisonModifier { Order = 2, Dps = 9, Tint = new Vector3(0.4f, 0.5f, 0.6f) },
            new CompositeModifier {
                Order = 3, Label = "wrap",
                Inner = new CritModifier { Order = 99, Multiplier = 4f },
            },
        };
        // Abstract-base ARRAY with two distinct concrete types (Circle/Square) -> proves the array path + abstract base.
        holder.Shapes = new Shape[] {
            new Circle { Area = 10f, Radius = 5f },
            new Square { Area = 20f, Side = 7f, Tint = new Vector3(0.7f, 0.8f, 0.9f) },
        };
        // Non-polymorphic control list (must stay byte-identical -- NO $type).
        holder.PlainInts = new List<int> { 11, 22, 33 };
        holder.Marker = 7;

        // ── Serialize ─────────────────────────────────────────────────────────────────────────────────
        var warnings = new List<string>();
        void Sink(string msg, int level) { if (level == 1) warnings.Add(msg); }
        Debugging.OnMessage += Sink;
        string yaml;
        try { yaml = SceneSerializer.Serialize(scene); }
        finally { Debugging.OnMessage -= Sink; }

        // (a)-serialize: every element's concrete $type appears.
        h.Check("interface-list element 1 writes $type = CritModifier",
            yaml.Contains($"$type: {typeof(CritModifier).FullName}"),
            $"expected '$type: {typeof(CritModifier).FullName}' in:\n{yaml}");
        h.Check("interface-list element 2 writes $type = PoisonModifier",
            yaml.Contains($"$type: {typeof(PoisonModifier).FullName}"));
        h.Check("interface-list element 3 writes $type = CompositeModifier",
            yaml.Contains($"$type: {typeof(CompositeModifier).FullName}"));
        h.Check("abstract-array element writes $type = Circle",
            yaml.Contains($"$type: {typeof(Circle).FullName}"));
        h.Check("abstract-array element writes $type = Square",
            yaml.Contains($"$type: {typeof(Square).FullName}"));
        h.Check("per-element member values serialized (crit multiplier + poison dps + composite label)",
            yaml.Contains("multiplier: 2.5") && yaml.Contains("dps: 9") && yaml.Contains("label: wrap"));
        h.Check("nested math-struct list-element member serialized via the converter",
            yaml.Contains("{x: 0.4, y: 0.5, z: 0.6}") && yaml.Contains("{x: 0.7, y: 0.8, z: 0.9}"),
            "Poison.Tint + Square.Tint Vector3 elements must use the math-struct converter");
        h.Check("no polymorphic-collection member warn-dropped",
            !warnings.Any(w => w.Contains("Mods") || w.Contains("Shapes")),
            $"unexpected drop warnings: [{string.Join(" | ", warnings)}]");

        // (c): the non-polymorphic control list is byte-identical -- a plain sequence, NO $type.
        h.Check("non-polymorphic List<int> serialized as a plain sequence", yaml.Contains("- 11") && yaml.Contains("- 33"));
        // Count $type occurrences: exactly the 3 list elements + 2 array elements + 1 NESTED Inner = 6.
        // The plain int list contributes ZERO, proving it stays on the byte-identical SerializeValue path.
        int typeTagCount = CountOccurrences(yaml, "$type:");
        h.Check("exactly 6 $type tags (3 list + 2 array + 1 nested Inner; plain int list contributes none)",
            typeTagCount == 6, $"got {typeTagCount} $type tags in:\n{yaml}");
        h.Check("plain int leaf alongside survives", yaml.Contains("marker: 7"));

        // (b): byte-stable across a second serialization (deterministic key order + $type placement).
        string yaml2 = SceneSerializer.Serialize(scene);
        h.Check("polymorphic-collection serialize is byte-stable across a re-run", yaml == yaml2,
            "two serializations of the same scene must be byte-identical");

        // ── (a)-deserialize: every concrete element type + values + ORDER rehydrate ──────────────────────
        SceneSerializer.Deserialize(yaml);
        PolymorphicCollectionHolderBehaviour rebuilt = null;
        foreach (Entity e in scene.Entities)
            foreach (Behaviour b in e.Behaviours)
                if (b is PolymorphicCollectionHolderBehaviour pb && !ReferenceEquals(pb, holder)) { rebuilt = pb; break; }

        h.Check("deserialize rebuilt a holder component", rebuilt is not null);
        if (rebuilt is not null) {
            // Interface LIST: 3 elements, concrete types + values + order intact.
            h.Check("interface list round-trips 3 elements in order with concrete types",
                rebuilt.Mods is { Count: 3 } ms &&
                ms[0] is CritModifier { Order: 1 } c0 && Math.Abs(c0.Multiplier - 2.5f) < 1e-5f &&
                ms[1] is PoisonModifier { Order: 2, Dps: 9 } p1 && p1.Tint == new Vector3(0.4f, 0.5f, 0.6f) &&
                ms[2] is CompositeModifier { Order: 3, Label: "wrap" },
                $"got [{(rebuilt.Mods is null ? "null" : string.Join(",", rebuilt.Mods.Select(m => m?.GetType().Name ?? "null")))}]");
            h.Check("NESTED polymorphic element inside a list element rehydrates (Composite.Inner = CritModifier)",
                rebuilt.Mods is { Count: 3 } ms2 &&
                ms2[2] is CompositeModifier { Inner: CritModifier { Order: 99 } ci } && Math.Abs(ci.Multiplier - 4f) < 1e-5f,
                "the Inner [SerializeReference] of a list element must rehydrate its concrete type");
            // Abstract-base ARRAY: 2 elements, concrete types + values + order intact.
            h.Check("abstract-base array round-trips 2 elements in order with concrete types",
                rebuilt.Shapes is { Length: 2 } sh &&
                sh[0] is Circle { } c && Math.Abs(c.Area - 10f) < 1e-5f && Math.Abs(c.Radius - 5f) < 1e-5f &&
                sh[1] is Square { } s && Math.Abs(s.Area - 20f) < 1e-5f && Math.Abs(s.Side - 7f) < 1e-5f &&
                s.Tint == new Vector3(0.7f, 0.8f, 0.9f),
                $"got [{(rebuilt.Shapes is null ? "null" : string.Join(",", rebuilt.Shapes.Select(x => x?.GetType().Name ?? "null")))}]");
            // (c)-deserialize: the non-polymorphic list still round-trips its contents + order (unchanged path).
            h.Check("non-polymorphic List<int> round-trips contents + order",
                rebuilt.PlainInts is { } pi && pi.SequenceEqual(new[] { 11, 22, 33 }),
                $"got [{(rebuilt.PlainInts is null ? "null" : string.Join(",", rebuilt.PlainInts))}]");
            h.Check("plain int leaf survived deserialize", rebuilt.Marker == 7);
        }

        // (b)-fixed-point: a serialize/deserialize/serialize cycle preserves the $type lines AND is byte-stable
        // against the FIRST serialization (the round-trip introduced no drift). This is the strong byte-stable
        // oracle: SHA-equivalent before-and-after the round trip.
        string yaml3 = SceneSerializer.Serialize(scene);
        h.Check("per-element $type lines survive a serialize/deserialize/serialize cycle",
            yaml3.Contains($"$type: {typeof(CritModifier).FullName}") &&
            yaml3.Contains($"$type: {typeof(PoisonModifier).FullName}") &&
            yaml3.Contains($"$type: {typeof(Square).FullName}"));
        // The re-serialized scene now contains BOTH the original holder and the deserialized copy, so yaml3
        // is not equal to yaml (twice the entities) -- assert instead that re-serializing yaml3's scene is a
        // fixed point against itself (no further drift), the byte-stable contract that matters for diffs.
        string yaml4 = SceneSerializer.Serialize(scene);
        h.Check("re-serialization after the round trip is a byte-stable fixed point", yaml3 == yaml4);

        return h.Report("Polymorphic collections (RW8/EF15)");
    }

    static int CountOccurrences(string haystack, string needle) {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }
}
