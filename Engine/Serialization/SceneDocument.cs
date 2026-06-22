
namespace BallisticEngine.Serialization;

public sealed class SceneDocument {
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Scene";

    public List<ComponentDocument> SceneComponents { get; set; } = new();

    public List<EntityDocument> Entities { get; set; } = new();
}

public sealed class EntityDocument {
    public string Id { get; set; }
    public string Name { get; set; }
    public bool IsActive { get; set; } = true;
    public string Tag { get; set; }
    public int Layer { get; set; }

    public string PrefabSource { get; set; }

    public TransformDocument Transform { get; set; } = new();
    public List<ComponentDocument> Components { get; set; } = new();
}

public sealed class TransformDocument {
    public Vector3 Position { get; set; } = Vector3.Zero;
    public Quaternion Rotation { get; set; } = Quaternion.Identity;
    public Vector3 Scale { get; set; } = Vector3.One;
    public string Parent { get; set; }
}

public sealed class ComponentDocument {
    public string Type { get; set; }

    public string Id { get; set; }

    public bool Enabled { get; set; } = true;
    public Dictionary<string, object> Members { get; set; } = new();
}
