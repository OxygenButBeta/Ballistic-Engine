
namespace BallisticEngine.Serialization;

public sealed class ComponentDocument {
    public string Type { get; set; }

    public string Id { get; set; }

    public bool Enabled { get; set; } = true;
    public Dictionary<string, object> Members { get; set; } = new();
}
