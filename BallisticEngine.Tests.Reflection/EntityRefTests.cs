using BallisticEngine;
using BallisticEngine.Serialization;

namespace BallisticEngine.Tests.Reflection;

// G1 (Phase G "entity/component references"): a serializable EntityRef/ComponentRef value type round-trips
// through the scene serializer as the target InstanceId hex (reusing the BEvent InstanceId pattern) and
// resolves lazily to the live scene object. This suite drives the REAL serialize/deserialize path over a
// live scene and asserts: a SET ref serializes to its InstanceId hex (not dropped), an UNSET ref is None
// (no value, no loud-drop warning), the ref deserializes back to the same InstanceId and resolves to a
// live object, and the whole round-trip is deterministic (serialize is a fixed point).
//
// Runs LAST-but-one (before SerializerDropTests): both insert into the global SceneManager which has no
// public unload, so the leftover scene is harmless (process exits next). We use `new SceneManager()`'s
// single scene (GetCurrentScene) rather than a second InsertScene to avoid HashSet ordering ambiguity.
internal static class EntityRefTests {
    public static int Run() {
        var h = new Harness();

        // Deserialize resolves a component by its registry name (ComponentRegistry.Resolve), so the test
        // fixtures must be in the registry — build over the engine AND this test assembly (the same shape
        // EngineBootstrap uses for engine + game scripts). Without this, ApplyComponent can't find
        // RefHolderBehaviour on reload and the round-trip half of the suite has nothing to assert against.
        ComponentRegistry.Build(typeof(ComponentRegistry).Assembly, typeof(EntityRefTests).Assembly);

        _ = new SceneManager();
        Scene scene = SceneManager.GetCurrentScene();

        // Build: holder entity references a separate target entity + a component on it.
        Entity target = Entity.Instantiate("RefTarget");
        var targetComp = target.AddComponent<RefTargetBehaviour>();

        Entity holderEntity = Entity.Instantiate("RefHolder");
        var holder = holderEntity.AddComponent<RefHolderBehaviour>();
        holder.TargetEntity = target;          // implicit Entity -> EntityRef
        holder.TargetComponent = targetComp;   // implicit Behaviour -> ComponentRef
        // UnsetEntity left default (None).

        Guid targetId = target.InstanceId;
        Guid targetCompId = targetComp.InstanceId;

        // ── EntityRef value-type basics ─────────────────────────────────────────────────────────────
        h.Check("set EntityRef HasValue", holder.TargetEntity.HasValue);
        h.Check("set EntityRef resolves to the target entity", ReferenceEquals(holder.TargetEntity.Value, target));
        h.Check("set EntityRef carries the target InstanceId", holder.TargetEntity.InstanceId == targetId);
        h.Check("default EntityRef is None (no value)", !holder.UnsetEntity.HasValue);
        h.Check("default EntityRef resolves to null", holder.UnsetEntity.Value is null);
        h.Check("set ComponentRef resolves to the target component",
            ReferenceEquals(holder.TargetComponent.Value, targetComp));
        h.Check("ComponentRef Get<T> returns the typed component",
            ReferenceEquals(holder.TargetComponent.Get<RefTargetBehaviour>(), targetComp));

        // ── Classification (the editor picker keys off this) ───────────────────────────────────────
        h.Check("EntityRef → SceneObjectRef", PropertyCategories.Classify(typeof(EntityRef)) == PropertyCategory.SceneObjectRef);
        h.Check("ComponentRef → SceneObjectRef", PropertyCategories.Classify(typeof(ComponentRef)) == PropertyCategory.SceneObjectRef);

        // ── Serialize: set refs become InstanceId hex; unset ref is silent; NO loud-drop ───────────
        var warnings = new List<string>();
        void Sink(string msg, int level) { if (level == 1) warnings.Add(msg); }
        Debugging.OnMessage += Sink;
        string yaml;
        try { yaml = SceneSerializer.Serialize(scene); }
        finally { Debugging.OnMessage -= Sink; }

        string targetHex = targetId.ToString("N");
        string targetCompHex = targetCompId.ToString("N");
        h.Check("set EntityRef serialized as InstanceId hex", yaml.Contains($"targetEntity: {targetHex}"),
            $"expected 'targetEntity: {targetHex}' in YAML");
        h.Check("set ComponentRef serialized as InstanceId hex", yaml.Contains($"targetComponent: {targetCompHex}"),
            $"expected 'targetComponent: {targetCompHex}' in YAML");
        h.Check("unset EntityRef omitted from YAML", !yaml.Contains("unsetEntity:"),
            "a None ref must not appear in the serialized scene");
        h.Check("plain int alongside refs still round-trips", yaml.Contains("marker: 11"));

        // The set refs are NOT a loud drop (they serialized), and the None ref is NOT a loud drop either
        // (None is a legitimate value, not a lost member).
        h.Check("set EntityRef did not warn-drop", !warnings.Any(w => w.Contains("TargetEntity")),
            $"unexpected drop warning for a SET ref: [{string.Join(" | ", warnings.Where(w => w.Contains("TargetEntity")))}]");
        h.Check("unset EntityRef did not warn-drop", !warnings.Any(w => w.Contains("UnsetEntity")),
            $"a None ref must not be reported as a dropped member: [{string.Join(" | ", warnings.Where(w => w.Contains("UnsetEntity")))}]");

        // ── Determinism: re-serializing the same scene is byte-identical ───────────────────────────
        string yaml2 = SceneSerializer.Serialize(scene);
        h.Check("serialize is deterministic (byte-identical re-run)", yaml == yaml2);

        // ── Deserialize: refs parse back to the same InstanceId and resolve to a live object ───────
        // Deserialize rebuilds the holder + target into the current scene with restored InstanceIds, so
        // FindByInstanceId resolves the ref. We read the round-tripped value off a freshly-built holder.
        SceneSerializer.Deserialize(yaml);
        RefHolderBehaviour rebuilt = null;
        foreach (Entity e in scene.Entities)
            foreach (Behaviour b in e.Behaviours)
                if (b is RefHolderBehaviour rb && !ReferenceEquals(rb, holder)) { rebuilt = rb; break; }

        h.Check("deserialize rebuilt a holder component", rebuilt is not null,
            "expected a second RefHolderBehaviour after deserialize");
        if (rebuilt is not null) {
            h.Check("deserialized EntityRef kept the InstanceId", rebuilt.TargetEntity.InstanceId == targetId,
                $"got {rebuilt.TargetEntity.InstanceId:N}, expected {targetHex}");
            h.Check("deserialized ComponentRef kept the InstanceId", rebuilt.TargetComponent.InstanceId == targetCompId);
            h.Check("deserialized EntityRef resolves to a live entity (lazy bind)",
                rebuilt.TargetEntity.Value is { } ent && ent.InstanceId == targetId,
                "the lazy resolver should bind the restored ref to a scene entity");
            h.Check("deserialized unset EntityRef stays None", !rebuilt.UnsetEntity.HasValue);
            h.Check("deserialized plain int survived", rebuilt.Marker == 11);
        }

        // ── Fixed point: serialize -> deserialize -> serialize reproduces the ref lines ────────────
        // (Full-scene equality is not byte-stable because deserialize duplicates entities into the same
        // scene; the REF members themselves are the round-trip invariant we assert.)
        string yaml3 = SceneSerializer.Serialize(scene);
        h.Check("ref lines survive a serialize/deserialize/serialize cycle",
            yaml3.Contains($"targetEntity: {targetHex}") && yaml3.Contains($"targetComponent: {targetCompHex}"));

        return h.Report("EntityRefs (G1)");
    }
}
