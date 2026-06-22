using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BallisticEngine;
using BallisticEngine.Serialization;

namespace BallisticEngine.Tests.Reflection;

// G3 (Phase G "[SerializeReference] polymorphism", ENGINE-HALF): a member whose declared type is an
// interface/abstract class marked [SerializeReference] round-trips its LIVE concrete type. The serializer
// writes a $type tag (the concrete FullName) + the instance's members; the deserializer resolves the
// concrete type via TypeCache, instantiates it, and refills its members through the SAME recursion a
// top-level component uses -- so a nested polymorphic member rehydrates too. This suite drives the REAL
// serialize/deserialize path over a live scene and asserts: the concrete type round-trips, member values
// survive, NESTED polymorphism recurses, a null ref writes no $type, a plain leaf is untouched, and the
// cycle is byte-identical/deterministic. byte-identical guarantee: a non-[SerializeReference] member never
// emits $type (no shipped component carries the attribute), so existing scenes are unaffected.
//
// Runs in Program.cs alongside the other G suites; like the others it inserts into the global SceneManager
// (no public unload) so it must run before SerializerDropTests.
internal static class SerializeReferenceTests {
    public static int Run() {
        var h = new Harness();

        // TypeCache resolves a $type FullName to a concrete type SCOPED to the declared base's implementors,
        // so it MUST include this test assembly (the fixtures) -- exactly as EngineBootstrap builds it over
        // engine + game scripts. ComponentRegistry is needed for the deserialize half (ApplyComponent by name).
        var engine = typeof(ComponentRegistry).Assembly;
        var tests = typeof(SerializeReferenceTests).Assembly;
        TypeCache.Build(engine, tests);
        ComponentRegistry.Build(engine, tests);

        _ = new SceneManager();
        Scene scene = SceneManager.GetCurrentScene();

        Entity holderEntity = Entity.Instantiate("PolyHolder");
        var holder = holderEntity.AddComponent<PolymorphicHolderBehaviour>();
        // Interface declared type -> a concrete CompositeModifier wrapping a NESTED PoisonModifier (recursion).
        holder.Mod = new CompositeModifier {
            Order = 3,
            Label = "outer",
            Inner = new PoisonModifier { Order = 9, Dps = 12, Tint = new Vector3(0.5f, 0.25f, 0.75f) },
        };
        // Abstract declared type -> a concrete BurnEffect.
        holder.Effect = new BurnEffect { Duration = 2.5f, Stacks = 4 };
        holder.Unset = null;       // null ref -> no $type, no value written
        holder.Marker = 7;

        // ── Serialize ─────────────────────────────────────────────────────────────────────────────────
        var warnings = new List<string>();
        void Sink(string msg, int level) { if (level == 1) warnings.Add(msg); }
        Debugging.OnMessage += Sink;
        string yaml;
        try { yaml = SceneSerializer.Serialize(scene); }
        finally { Debugging.OnMessage -= Sink; }

        h.Check("interface member writes $type = concrete FullName",
            yaml.Contains($"$type: {typeof(CompositeModifier).FullName}"),
            $"expected '$type: {typeof(CompositeModifier).FullName}' in:\n{yaml}");
        h.Check("abstract member writes $type = concrete FullName",
            yaml.Contains($"$type: {typeof(BurnEffect).FullName}"));
        h.Check("NESTED polymorphic member writes its own $type (recursion)",
            yaml.Contains($"$type: {typeof(PoisonModifier).FullName}"),
            "the Inner field's concrete type must be tagged inside the outer mapping");
        h.Check("concrete member values serialized (outer label + nested dps)",
            yaml.Contains("label: outer") && yaml.Contains("dps: 12"));
        h.Check("nested math-struct member serialized via the converter",
            yaml.Contains("{x: 0.5, y: 0.25, z: 0.75}"),
            "the nested PoisonModifier.Tint Vector3 must use the math-struct converter");
        h.Check("null polymorphic member writes NO $type and NO value",
            !yaml.Contains("unset:"),
            "an unset [SerializeReference] member must not appear in the YAML at all");
        h.Check("plain int leaf alongside polymorphic members survives", yaml.Contains("marker: 7"));
        h.Check("no polymorphic member warn-dropped",
            !warnings.Any(w => w.Contains("Mod") || w.Contains("Effect")),
            $"unexpected drop warnings: [{string.Join(" | ", warnings)}]");

        // ── Determinism ────────────────────────────────────────────────────────────────────────────────
        string yaml2 = SceneSerializer.Serialize(scene);
        h.Check("polymorphic serialize is deterministic (byte-identical re-run)", yaml == yaml2);

        // ── Deserialize: the concrete type + members + nested recursion rehydrate ─────────────────────────
        SceneSerializer.Deserialize(yaml);
        PolymorphicHolderBehaviour rebuilt = null;
        foreach (Entity e in scene.Entities)
            foreach (Behaviour b in e.Behaviours)
                if (b is PolymorphicHolderBehaviour pb && !ReferenceEquals(pb, holder)) { rebuilt = pb; break; }

        h.Check("deserialize rebuilt a holder component", rebuilt is not null);
        if (rebuilt is not null) {
            h.Check("interface member rehydrates the CONCRETE type",
                rebuilt.Mod is CompositeModifier,
                $"got {(rebuilt.Mod?.GetType().Name ?? "null")} (expected CompositeModifier)");
            h.Check("concrete member values round-trip",
                rebuilt.Mod is CompositeModifier { Order: 3, Label: "outer" });
            h.Check("NESTED polymorphic member rehydrates its concrete type + values",
                rebuilt.Mod is CompositeModifier { Inner: PoisonModifier { Order: 9, Dps: 12 } pm } &&
                pm.Tint == new Vector3(0.5f, 0.25f, 0.75f),
                "the Inner field must come back as a PoisonModifier with its members intact");
            h.Check("abstract member rehydrates the concrete subclass + values",
                rebuilt.Effect is BurnEffect { Stacks: 4 } be && Math.Abs(be.Duration - 2.5f) < 1e-5f,
                $"got {(rebuilt.Effect?.GetType().Name ?? "null")}");
            h.Check("null polymorphic member stays null", rebuilt.Unset is null);
            h.Check("plain int leaf survived deserialize", rebuilt.Marker == 7);
        }

        // ── Unresolvable $type is lenient (drops to null, no throw) ───────────────────────────────────────
        // Hand-craft a member dict with a bogus $type and run it through the same deserialize entry the
        // serializer's output uses, asserting it neither throws nor binds an unrelated type.
        {
            var bogus = new Dictionary<string, object> {
                ["mod"] = new Dictionary<object, object> { ["$type"] = "Nonexistent.Type.Name", ["order"] = 1 },
                ["marker"] = 99,
            };
            string yamlBogus = SerializeBogusHolder(bogus);
            var beforeWarn = new List<string>();
            void Sink2(string msg, int level) { if (level == 1) beforeWarn.Add(msg); }
            Debugging.OnMessage += Sink2;
            try { SceneSerializer.Deserialize(yamlBogus); }
            catch (Exception ex) { h.Check("unresolvable $type does not throw", false, ex.Message); }
            finally { Debugging.OnMessage -= Sink2; }
            PolymorphicHolderBehaviour bogusRebuilt = scene.Entities
                .SelectMany(e => e.Behaviours).OfType<PolymorphicHolderBehaviour>()
                .FirstOrDefault(b => b.Marker == 99);
            h.Check("unresolvable $type -> member dropped to null, leaf kept",
                bogusRebuilt is { Mod: null, Marker: 99 });
            h.Check("unresolvable $type logged a warning",
                beforeWarn.Any(w => w.Contains("Nonexistent.Type.Name")),
                $"expected a resolve warning, got [{string.Join(" | ", beforeWarn)}]");
        }

        // ── Fixed point: the $type lines survive a serialize/deserialize/serialize cycle ──────────────────
        string yaml3 = SceneSerializer.Serialize(scene);
        h.Check("polymorphic lines survive a serialize/deserialize/serialize cycle",
            yaml3.Contains($"$type: {typeof(CompositeModifier).FullName}") &&
            yaml3.Contains($"$type: {typeof(PoisonModifier).FullName}"));

        return h.Report("SerializeReference (G3 engine-half)");
    }

    // Build a one-entity scene YAML carrying a hand-authored PolymorphicHolderBehaviour member dict, so the
    // bogus-$type leniency path is exercised through the real Deserialize entry (the same shape the serializer
    // emits). Uses the shared SceneYaml serializer so the document round-trips through YamlDotNet identically.
    static string SerializeBogusHolder(Dictionary<string, object> members) {
        var doc = new SceneDocument {
            Entities = {
                new EntityDocument {
                    Name = "BogusHolder",
                    Components = { new ComponentDocument {
                        Type = ComponentRegistry.NameOf(new PolymorphicHolderBehaviour()),
                        Enabled = true,
                        Members = members,
                    } },
                },
            },
        };
        return SceneYaml.Serializer.Serialize(doc);
    }
}
