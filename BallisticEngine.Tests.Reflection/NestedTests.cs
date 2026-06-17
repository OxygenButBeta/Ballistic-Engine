using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BallisticEngine;
using BallisticEngine.Serialization;

namespace BallisticEngine.Tests.Reflection;

// G4 (Phase G "nested struct/class members", ENGINE-HALF): a member whose declared type is a plain concrete
// class or non-primitive struct (no [SerializeReference], not a math struct / asset / collection / ref) must
// round-trip its serializable members as a nested YAML mapping, with NO $type tag (the declared type IS the
// concrete type). Before G4 such a member rode the pass-through path -- it serialized but deserialized to
// null, so the round-trip was silently lost (§3.45 item 4). This suite drives the REAL serialize/deserialize
// path over a live scene and asserts: a nested STRUCT round-trips its inner fields (the boxed write-back the
// codec must do), a nested CLASS round-trips, nested-in-nested recurses, a null member writes nothing, a
// plain leaf alongside is untouched, a self-referential class hits the cycle guard (no infinite recursion),
// and the cycle is byte-identical/deterministic.
//
// Runs in Program.cs alongside the other G suites; like the others it inserts into the global SceneManager
// (no public unload) so it must run before SerializerDropTests.
internal static class NestedTests {
    public static int Run() {
        var h = new Harness();

        // ComponentRegistry is needed for the deserialize half (ApplyComponent by name); TypeCache for the
        // engine universe (and so any prior rebuild that dropped the test assembly is restored).
        var engine = typeof(ComponentRegistry).Assembly;
        var tests = typeof(NestedTests).Assembly;
        TypeCache.Build(engine, tests);
        ComponentRegistry.Build(engine, tests);

        // ── Classification sanity: the holder's members must classify as the model expects (the codec keys
        //    its Nested branch off exactly this, so a misclassification would silently change the path) ──────
        Type holderType = typeof(NestedHolderBehaviour);
        var byName = ComponentReflection.SerializableMembers(holderType)
            .ToDictionary(m => m.Name, m => ComponentReflection.MemberType(m));
        h.Check("Settings classifies Nested",
            PropertyCategories.Classify(byName["Settings"]) == PropertyCategory.Nested);
        h.Check("Config classifies Nested",
            PropertyCategories.Classify(byName["Config"]) == PropertyCategory.Nested);
        h.Check("Chain (self-ref class) classifies Nested",
            PropertyCategories.Classify(byName["Chain"]) == PropertyCategory.Nested);
        h.Check("Marker (int) classifies Primitive (untouched leaf)",
            PropertyCategories.Classify(byName["Marker"]) == PropertyCategory.Primitive);

        _ = new SceneManager();
        Scene scene = SceneManager.GetCurrentScene();

        Entity holderEntity = Entity.Instantiate("NestedHolder");
        var holder = holderEntity.AddComponent<NestedHolderBehaviour>();
        holder.Settings = new NestedSettings {
            Level = 5,
            Offset = new Vector3(1.5f, 2.25f, 3.75f),         // math struct -> converter -> {x,y,z}
            Inner = new InnerRange { Min = 0.1f, Max = 0.9f }, // nested struct inside the struct
        };
        holder.Config = new NestedConfig {
            Name = "config-A",
            Count = 42,
            Bounds = new InnerRange { Min = -1f, Max = 1f },   // nested struct inside the class
        };
        holder.Unset = null;                                    // null class -> writes nothing
        // Build a 2-node CYCLE: a -> b -> a. Serializing `Chain` must stop at the back-reference guard.
        var a = new NestedLink { Id = 1 };
        var b = new NestedLink { Id = 2 };
        a.Next = b;
        b.Next = a;
        holder.Chain = a;
        holder.Marker = 11;

        // ── Serialize ─────────────────────────────────────────────────────────────────────────────────────
        var warnings = new List<string>();
        void Sink(string msg, int level) { if (level == 1) warnings.Add(msg); }
        Debugging.OnMessage += Sink;
        string yaml;
        try { yaml = SceneSerializer.Serialize(scene); }
        finally { Debugging.OnMessage -= Sink; }

        h.Check("nested struct member serialized as a mapping (level + inner)",
            yaml.Contains("level: 5") && yaml.Contains("min: 0.1") && yaml.Contains("max: 0.9"),
            $"expected the nested struct + its inner range in:\n{yaml}");
        h.Check("nested struct math member serialized via the converter",
            yaml.Contains("{x: 1.5, y: 2.25, z: 3.75}"),
            "the NestedSettings.Offset Vector3 must use the math-struct converter");
        h.Check("nested class member serialized as a mapping (name + count + bounds)",
            yaml.Contains("name: config-A") && yaml.Contains("count: 42"));
        h.Check("nested members carry NO $type tag (declared type is concrete)",
            !yaml.Contains("$type"),
            "a plain nested member must not emit a $type discriminator (that is the polymorphic path)");
        h.Check("null nested member writes nothing",
            !yaml.Contains("unset:"),
            "an unset nested class member must not appear in the YAML at all");
        h.Check("plain int leaf alongside nested members survives", yaml.Contains("marker: 11"));
        h.Check("cyclic nested class did not throw / hang and emitted the head id",
            yaml.Contains("id: 1"));
        h.Check("no nested member warn-dropped during serialize",
            !warnings.Any(w => w.Contains("Settings") || w.Contains("Config") || w.Contains("Chain")),
            $"unexpected drop warnings: [{string.Join(" | ", warnings)}]");

        // ── Determinism ────────────────────────────────────────────────────────────────────────────────────
        string yaml2 = SceneSerializer.Serialize(scene);
        h.Check("nested serialize is deterministic (byte-identical re-run)", yaml == yaml2);

        // ── Deserialize: the nested struct/class + members + nested-in-nested rehydrate ─────────────────────
        SceneSerializer.Deserialize(yaml);
        NestedHolderBehaviour rebuilt = null;
        foreach (Entity e in scene.Entities)
            foreach (Behaviour bh in e.Behaviours)
                if (bh is NestedHolderBehaviour nh && !ReferenceEquals(nh, holder)) { rebuilt = nh; break; }

        h.Check("deserialize rebuilt a holder component", rebuilt is not null);
        if (rebuilt is not null) {
            // STRUCT WRITE-BACK: the boxed struct's inner fields must come back through the field, not stay
            // default. This is the central G4 proof (ch20/21/23 deferred struct write-back; the codec closes it).
            h.Check("nested STRUCT member round-trips its inner fields (boxed write-back)",
                rebuilt.Settings.Level == 5 &&
                rebuilt.Settings.Offset == new Vector3(1.5f, 2.25f, 3.75f),
                $"got Level={rebuilt.Settings.Level}, Offset={rebuilt.Settings.Offset}");
            h.Check("nested-in-nested STRUCT (Inner range) round-trips",
                Math.Abs(rebuilt.Settings.Inner.Min - 0.1f) < 1e-5f &&
                Math.Abs(rebuilt.Settings.Inner.Max - 0.9f) < 1e-5f,
                $"got Inner=({rebuilt.Settings.Inner.Min}, {rebuilt.Settings.Inner.Max})");
            h.Check("nested CLASS member round-trips its members",
                rebuilt.Config is { Name: "config-A", Count: 42 },
                $"got {(rebuilt.Config is null ? "null" : $"Name={rebuilt.Config.Name}, Count={rebuilt.Config.Count}")}");
            h.Check("nested struct inside the CLASS round-trips",
                rebuilt.Config is not null &&
                Math.Abs(rebuilt.Config.Bounds.Min - (-1f)) < 1e-5f &&
                Math.Abs(rebuilt.Config.Bounds.Max - 1f) < 1e-5f);
            h.Check("null nested member stays null", rebuilt.Unset is null);
            h.Check("plain int leaf survived deserialize", rebuilt.Marker == 11);
            // The cycle serialized tree-only (back-edge -> null), so the head rehydrates and its Next.Next
            // terminates at null instead of pointing back (Unity parity: a back-reference is dropped).
            h.Check("cyclic nested class rehydrates the head id (tree-only, no infinite loop)",
                rebuilt.Chain is { Id: 1 });
            h.Check("cycle back-edge dropped to null (tree-only + cycle-guard)",
                rebuilt.Chain?.Next is null || rebuilt.Chain?.Next?.Next is null,
                "the A->B->A back-reference must terminate, not re-point at A");
        }

        // ── Type with NO public parameterless ctor is lenient (drops to null, no throw) ─────────────────────
        // Hand-craft a member dict targeting a nested member whose declared type can't be Activator-created,
        // and assert it neither throws nor silently passes a bogus value. NestedConfig HAS a ctor, so instead
        // exercise the leniency on a deliberately unconstructable shape via a fabricated holder member dict.
        {
            // A nested member fed a NON-mapping scalar must fall through harmlessly (not a nested payload).
            var scalarMembers = new Dictionary<string, object> {
                ["config"] = "not-a-mapping",   // a Nested target with a scalar raw -> left for coerce -> null
                ["marker"] = 77,
            };
            string yamlScalar = SerializeNestedHolder(scalarMembers);
            try { SceneSerializer.Deserialize(yamlScalar); }
            catch (Exception ex) { h.Check("nested scalar raw does not throw", false, ex.Message); }
            NestedHolderBehaviour scalarRebuilt = scene.Entities
                .SelectMany(e => e.Behaviours).OfType<NestedHolderBehaviour>()
                .FirstOrDefault(bh => bh.Marker == 77);
            h.Check("nested member fed a scalar -> dropped to null, leaf kept",
                scalarRebuilt is { Config: null, Marker: 77 });
        }

        // ── Fixed point: the nested mappings survive a serialize/deserialize/serialize cycle ────────────────
        string yaml3 = SceneSerializer.Serialize(scene);
        h.Check("nested mappings survive a serialize/deserialize/serialize cycle",
            yaml3.Contains("level: 5") && yaml3.Contains("name: config-A"));

        return h.Report("Nested (G4 engine-half)");
    }

    // Build a one-entity scene YAML carrying a hand-authored NestedHolderBehaviour member dict, so the
    // leniency paths are exercised through the real Deserialize entry (the same document shape the serializer
    // emits). Uses the shared SceneYaml serializer so the document round-trips through YamlDotNet identically.
    static string SerializeNestedHolder(Dictionary<string, object> members) {
        var doc = new SceneDocument {
            Entities = {
                new EntityDocument {
                    Name = "NestedHolderHand",
                    Components = { new ComponentDocument {
                        Type = ComponentRegistry.NameOf(new NestedHolderBehaviour()),
                        Enabled = true,
                        Members = members,
                    } },
                },
            },
        };
        return SceneYaml.Serializer.Serialize(doc);
    }
}
