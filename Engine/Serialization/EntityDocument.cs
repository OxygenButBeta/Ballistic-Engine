
namespace BallisticEngine.Serialization;

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
