using OpenTK.Mathematics;

namespace BallisticEngine.Serialization;

// Plain DTOs that mirror a .scene file's YAML shape. The serializer projects live
// entities/components into these and back; component members live in a string->object
// map so any registered Behaviour serializes without a bespoke DTO.
public sealed class SceneDocument {
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Scene";
    public List<EntityDocument> Entities { get; set; } = new();
}

public sealed class EntityDocument {
    public string Id { get; set; }            // file-local id, used to wire transform parents
    public string Name { get; set; }
    public bool IsActive { get; set; } = true;
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
    public bool Enabled { get; set; } = true;
    public Dictionary<string, object> Members { get; set; } = new();
}
