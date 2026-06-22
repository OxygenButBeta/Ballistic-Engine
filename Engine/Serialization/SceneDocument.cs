
namespace BallisticEngine.Serialization;

public sealed class SceneDocument {
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Scene";

    public List<ComponentDocument> SceneComponents { get; set; } = new();

    public List<EntityDocument> Entities { get; set; } = new();
}
