
namespace BallisticEngine.Serialization;

// Plain DTOs that mirror a .scene file's YAML shape. The serializer projects live
// entities/components into these and back; component members live in a string->object
// map so any registered Behaviour serializes without a bespoke DTO.
public sealed class SceneDocument {
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Scene";

    // Scene-wide components (SceneBehaviours: skybox, fog, ...), not attached to any entity.
    public List<ComponentDocument> SceneComponents { get; set; } = new();

    public List<EntityDocument> Entities { get; set; } = new();
}

public sealed class EntityDocument {
    public string Id { get; set; }            // file-local id, used to wire transform parents
    public string Name { get; set; }
    public bool IsActive { get; set; } = true;
    public string Tag { get; set; }           // null/"Untagged" omitted for clean diffs
    public int Layer { get; set; }            // 0 ("Default") omitted by the serializer
    public string PrefabSource { get; set; }  // 32-hex GUID of the source .prefab if this is a prefab
                                              // instance root; null for plain entities (omitted)
    public TransformDocument Transform { get; set; } = new();
    public List<ComponentDocument> Components { get; set; } = new();
}

public sealed class TransformDocument {
    public Vector3 Position { get; set; } = Vector3.Zero;
    public Quaternion Rotation { get; set; } = Quaternion.Identity;
    public Vector3 Scale { get; set; } = Vector3.One;
    public string Parent { get; set; }        // file-local id of the parent entity, or null
}

public sealed class ComponentDocument {
    public string Type { get; set; }          // ComponentRegistry key
    public string Id { get; set; }            // InstanceId (32-hex); restored on load so BEvent
                                              // listeners targeting this component rebind across
                                              // reload/undo. Null on legacy scenes (fresh id assigned).
    public bool Enabled { get; set; } = true;
    public Dictionary<string, object> Members { get; set; } = new();
}
