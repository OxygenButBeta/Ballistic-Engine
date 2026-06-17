using BallisticEngine;
using BallisticEngine.Serialization;

namespace BallisticEngine.Tests.Reflection;

// G0 (Phase G "loud drops"): a serializable member that HOLDS a value but produces no serialized form
// (a scene-object reference with no asset guid, an unsupported type) must be reported LOUDLY, not dropped
// to null in silence (the §3.45 silent-failure trap). This suite drives the REAL public serialize path
// (SceneSerializer.Serialize over a live scene) and asserts the warning fires, names the right member,
// never fires for a healthy member, and is deduped to one report per (type, member).
internal static class SerializerDropTests {
    public static int Run() {
        var h = new Harness();

        // A live scene with one entity carrying the fixture. `Linked` references a Behaviour (a BObject
        // with no AssetDatabase guid) → it has no serialized form. `Healthy` is a plain int → it survives.
        // `new SceneManager()` wires the static `instance` AND creates the single active scene; we serialize
        // THAT scene (activeScenes is a HashSet, so we take GetCurrentScene rather than a second insert to
        // avoid ordering ambiguity). Entity.Instantiate registers into it. This suite runs LAST; the
        // SceneManager has no public unload API, so the leftover scene is harmless (process exits next).
        _ = new SceneManager();
        Scene scene = SceneManager.GetCurrentScene();
        {
            Entity owner = Entity.Instantiate("DropOwner");
            var fixture = owner.AddComponent<DropFixtureBehaviour>();
            var target = owner.AddComponent<DropTargetBehaviour>();
            fixture.Linked = target;   // scene-object ref, no guid → the drop under test
            fixture.Healthy = 42;      // round-trips → must NOT warn

            // Capture every warning emitted during the serialize (Debugging.OnMessage level 1 = warning).
            var warnings = new List<string>();
            void Sink(string msg, int level) { if (level == 1) warnings.Add(msg); }
            Debugging.OnMessage += Sink;

            string yaml;
            try {
                yaml = SceneSerializer.Serialize(scene);
            } finally {
                Debugging.OnMessage -= Sink;
            }

            // ── The drop is LOUD ────────────────────────────────────────────────────────────────────
            bool warnedLinked = warnings.Any(w => w.Contains("Linked"));
            h.Check("dropped scene-object member produced a warning", warnedLinked,
                $"expected a warning naming 'Linked'; got [{string.Join(" | ", warnings)}]");

            // The warning is actionable: it names the owning component + that the value is lost.
            h.Check("warning names the owning component", warnings.Any(w => w.Contains("DropFixtureBehaviour")));
            h.Check("warning says the value is lost", warnings.Any(w => w.Contains("lost on reload")));

            // ── A healthy member is NEVER reported ──────────────────────────────────────────────────
            h.Check("healthy member did not warn", !warnings.Any(w => w.Contains("Healthy")));

            // ── The warning matches reality: the YAML really dropped Linked but kept Healthy ─────────
            // (camelCase member names in the YAML map; the int survives, the ref does not.)
            h.Check("dropped member absent from YAML", !yaml.Contains("linked:"),
                "the scene-object ref should not appear in the serialized scene");
            h.Check("healthy member present in YAML", yaml.Contains("healthy: 42"),
                "the plain int member should round-trip into the YAML");

            // ── Dedup: a SECOND serialize of the same drop produces NO new warning (warn-once) ──────
            var second = new List<string>();
            void Sink2(string msg, int level) { if (level == 1 && msg.Contains("Linked")) second.Add(msg); }
            Debugging.OnMessage += Sink2;
            try {
                SceneSerializer.Serialize(scene);
            } finally {
                Debugging.OnMessage -= Sink2;
            }
            h.Check("re-serialize does not re-warn the same drop (deduped)", second.Count == 0,
                $"expected 0 repeat warnings, got {second.Count}");
        }

        return h.Report("SerializerDrops (G0)");
    }
}
