using BallisticEngine.Serialization;

namespace BallisticEngine;

// A reusable entity blueprint (Unity's Prefab) — a saved entity subtree (root + descendants +
// components) that Instantiate clones into the scene. Stored as a .prefab YAML asset (same shape as
// the entities block of a .scene), loaded via AssetDatabase.Load<PrefabAsset>("Assets/...prefab").
//
// The asset holds the captured EntityDocuments; Instantiate rebuilds them through the scene
// serializer so every component, asset ref, and child round-trips exactly as it would from a scene.
// Each instantiation gets fresh instance ids — two instances never share identity.
public sealed class PrefabAsset : BObject {
    // The captured subtree (root first). Public so the editor can inspect/replace it; game code
    // just calls Instantiate.
    public List<EntityDocument> Entities { get; }

    // GUID of the .prefab asset this was loaded from (set by PrefabLoader). Stamped onto every
    // instance's root as Entity.PrefabSource so the link resolves back to this asset. Guid.Empty for
    // a prefab built in-memory (FromEntity) that hasn't been imported yet.
    public Guid SourceGuid { get; set; } = Guid.Empty;

    public PrefabAsset(List<EntityDocument> entities) {
        Entities = entities ?? new List<EntityDocument>();
        Name = Entities.Count > 0 ? Entities[0].Name ?? "Prefab" : "Prefab";
    }

    // Clones this prefab into the current scene at its authored transform; returns the root entity and
    // links it back to this asset (Entity.PrefabSource) so the editor renders it as a prefab instance.
    public Entity Instantiate() {
        Entity root = SceneSerializer.InstantiateSubtree(Entities);
        if (root is not null && SourceGuid != Guid.Empty)
            root.PrefabSource = SourceGuid;
        return root;
    }

    // Clones at a specific world position/rotation (the common spawn case).
    public Entity Instantiate(Vector3 position, Quaternion rotation) {
        Entity root = Instantiate();
        if (root is not null) {
            root.transform.WorldPosition = position;
            root.transform.WorldRotation = rotation;
        }
        return root;
    }

    // Clones as a child of `parent`, keeping the prefab's local transform.
    public Entity Instantiate(Transform parent) {
        Entity root = Instantiate();
        if (root is not null && parent is not null)
            root.transform.SetParent(parent);
        return root;
    }

    // ---- Authoring (editor) -----------------------------------------------------------------

    // Captures a live entity subtree into a new prefab asset (editor "Create Prefab from selection").
    public static PrefabAsset FromEntity(Entity root) =>
        new(SceneSerializer.CaptureSubtree(root));

    // Serializes this prefab to YAML for writing a .prefab file (used by the editor on create).
    public string ToYaml() {
        var doc = new SceneDocument { Name = Name, Entities = Entities };
        return SceneYaml.Serializer.Serialize(doc);
    }

    // Parses a .prefab's YAML into a prefab asset (used by the loader).
    public static PrefabAsset FromYaml(string yaml) {
        SceneDocument doc = SceneYaml.Deserializer.Deserialize<SceneDocument>(yaml);
        return new PrefabAsset(doc?.Entities ?? new List<EntityDocument>());
    }
}
