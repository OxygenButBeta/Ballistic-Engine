using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BallisticEngine;
using BallisticEngine.Serialization;

namespace BallisticEngine.Tests.Reflection;

// G2 (Phase G "collections"): List<T> / arrays / Dictionary<K,V> component members round-trip through the
// scene serializer by recursing EACH element through the SAME value pipeline (SerializeValue/DeserializeValue)
// the leaf members use -- so an element can be a primitive, a math struct, or a scene-object ref. Before G2
// these silently deserialized to null (round-trip loss, the §3.45 gap); this suite drives the REAL
// serialize/deserialize path over a live scene and asserts each collection shape survives, an empty list
// stays empty (not null), element ORDER is preserved, the round-trip is deterministic, and a plain leaf
// alongside is untouched.
//
// Runs in Program.cs alongside the other G suites; like EntityRefTests it inserts into the global
// SceneManager (no public unload) so it runs before SerializerDropTests.
internal static class CollectionTests {
    public static int Run() {
        var h = new Harness();

        // ApplyComponent resolves a component by its registry name, so the fixtures must be in the registry
        // (the same engine + test-assembly shape EngineBootstrap uses) for the deserialize round-trip half.
        ComponentRegistry.Build(typeof(ComponentRegistry).Assembly, typeof(CollectionTests).Assembly);

        _ = new SceneManager();
        Scene scene = SceneManager.GetCurrentScene();

        // A separate target entity for the List<EntityRef> element-recursion check.
        Entity refTarget = Entity.Instantiate("CollRefTarget");
        Guid refTargetId = refTarget.InstanceId;

        Entity holderEntity = Entity.Instantiate("CollHolder");
        var holder = holderEntity.AddComponent<CollectionHolderBehaviour>();
        holder.Ints = new List<int> { 10, 20, 30 };
        holder.Points = new List<Vector3> { new(1, 2, 3), new(-4, 5, -6) };
        holder.Names = new[] { "alpha", "beta", "gamma" };
        holder.Scores = new Dictionary<string, int> { ["hp"] = 100, ["mp"] = 42 };
        holder.Targets = new List<EntityRef> { refTarget };       // implicit Entity -> EntityRef
        holder.EmptyList = new List<int>();                       // authored empty

        // ── Serialize ───────────────────────────────────────────────────────────────────────────────
        var warnings = new List<string>();
        void Sink(string msg, int level) { if (level == 1) warnings.Add(msg); }
        Debugging.OnMessage += Sink;
        string yaml;
        try { yaml = SceneSerializer.Serialize(scene); }
        finally { Debugging.OnMessage -= Sink; }

        h.Check("List<int> serialized as a sequence", yaml.Contains("ints:") && yaml.Contains("- 10"),
            $"expected an 'ints:' sequence with '- 10' in:\n{yaml}");
        h.Check("List<Vector3> elements serialized as flow maps",
            yaml.Contains("{x: 1, y: 2, z: 3}") && yaml.Contains("{x: -4, y: 5, z: -6}"),
            "Vector3 list elements must use the math-struct converter on the boxed runtime type");
        h.Check("array of strings serialized", yaml.Contains("- alpha") && yaml.Contains("- gamma"));
        h.Check("Dictionary serialized as a mapping", yaml.Contains("hp: 100") && yaml.Contains("mp: 42"));
        h.Check("List<EntityRef> element serialized as InstanceId hex",
            yaml.Contains($"- {refTargetId:N}"), $"expected '- {refTargetId:N}' (the ref element) in the YAML");
        // No collection member is a loud-drop: every one produced a serialized form.
        h.Check("no collection member warn-dropped",
            !warnings.Any(w => w.Contains("Ints") || w.Contains("Points") || w.Contains("Names") ||
                               w.Contains("Scores") || w.Contains("Targets") || w.Contains("EmptyList")),
            $"unexpected drop warnings: [{string.Join(" | ", warnings)}]");
        h.Check("plain int leaf alongside collections survives", yaml.Contains("marker: 7"));

        // ── Determinism ──────────────────────────────────────────────────────────────────────────────
        string yaml2 = SceneSerializer.Serialize(scene);
        h.Check("collection serialize is deterministic (byte-identical re-run)", yaml == yaml2);

        // ── Deserialize: every collection rehydrates with the right contents + order ──────────────────
        SceneSerializer.Deserialize(yaml);
        CollectionHolderBehaviour rebuilt = null;
        foreach (Entity e in scene.Entities)
            foreach (Behaviour b in e.Behaviours)
                if (b is CollectionHolderBehaviour cb && !ReferenceEquals(cb, holder)) { rebuilt = cb; break; }

        h.Check("deserialize rebuilt a holder component", rebuilt is not null);
        if (rebuilt is not null) {
            h.Check("List<int> round-trips contents + order",
                rebuilt.Ints is { } li && li.SequenceEqual(new[] { 10, 20, 30 }),
                $"got [{(rebuilt.Ints is null ? "null" : string.Join(",", rebuilt.Ints))}]");
            h.Check("List<Vector3> round-trips contents + order",
                rebuilt.Points is { Count: 2 } lp && lp[0] == new Vector3(1, 2, 3) && lp[1] == new Vector3(-4, 5, -6),
                $"got [{(rebuilt.Points is null ? "null" : string.Join(";", rebuilt.Points))}]");
            h.Check("string[] round-trips contents + order",
                rebuilt.Names is { Length: 3 } na && na[0] == "alpha" && na[1] == "beta" && na[2] == "gamma",
                $"got [{(rebuilt.Names is null ? "null" : string.Join(",", rebuilt.Names))}]");
            h.Check("Dictionary round-trips contents",
                rebuilt.Scores is { Count: 2 } sc && sc["hp"] == 100 && sc["mp"] == 42,
                $"got [{(rebuilt.Scores is null ? "null" : string.Join(",", rebuilt.Scores.Select(kv => $"{kv.Key}={kv.Value}")))}]");
            h.Check("List<EntityRef> element round-trips + resolves to the live target",
                rebuilt.Targets is { Count: 1 } lt && lt[0].InstanceId == refTargetId &&
                lt[0].Value is { } ent && ent.InstanceId == refTargetId,
                "the ref element must keep its InstanceId and lazily resolve to a scene entity");
            h.Check("authored empty list round-trips as empty (NOT null)",
                rebuilt.EmptyList is { Count: 0 },
                rebuilt.EmptyList is null ? "EmptyList came back null -- empty must round-trip empty" : "ok");
            h.Check("plain int leaf survived deserialize", rebuilt.Marker == 7);
        }

        // ── Fixed point: the collection lines survive a serialize/deserialize/serialize cycle ─────────
        string yaml3 = SceneSerializer.Serialize(scene);
        h.Check("collection lines survive a serialize/deserialize/serialize cycle",
            yaml3.Contains("- 10") && yaml3.Contains("{x: 1, y: 2, z: 3}") && yaml3.Contains("hp: 100"));

        return h.Report("Collections (G2)");
    }
}
