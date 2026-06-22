using BallisticEngine.Serialization;

namespace BallisticEngine;

public sealed class PrefabAsset : BObject {
    public List<EntityDocument> Entities { get; }

    public Guid SourceGuid { get; set; } = Guid.Empty;

    public PrefabAsset(List<EntityDocument> entities) {
        Entities = entities ?? new List<EntityDocument>();
        Name = Entities.Count > 0 ? Entities[0].Name ?? "Prefab" : "Prefab";
    }

    public Entity Instantiate() {
        Entity root = SceneSerializer.InstantiateSubtree(Entities);
        if (root is not null && SourceGuid != Guid.Empty)
            root.PrefabSource = SourceGuid;
        return root;
    }

    public Entity Instantiate(Vector3 position, Quaternion rotation) {
        Entity root = Instantiate();
        if (root is not null) {
            root.transform.WorldPosition = position;
            root.transform.WorldRotation = rotation;
        }
        return root;
    }

    public Entity Instantiate(Transform parent) {
        Entity root = Instantiate();
        if (root is not null && parent is not null)
            root.transform.SetParent(parent);
        return root;
    }

    public static PrefabAsset FromEntity(Entity root) =>
        new(SceneSerializer.CaptureSubtree(root));

    public string ToYaml() {
        var doc = new SceneDocument { Name = Name, Entities = Entities };
        return SceneYaml.Serializer.Serialize(doc);
    }

    public static PrefabAsset FromYaml(string yaml) {
        SceneDocument doc = SceneYaml.Deserializer.Deserialize<SceneDocument>(yaml);
        return new PrefabAsset(doc?.Entities ?? new List<EntityDocument>());
    }
}
