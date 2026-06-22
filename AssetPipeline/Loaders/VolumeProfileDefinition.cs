using System.Text.Json;

namespace BallisticEngine.AssetPipeline.Loaders;

public sealed class VolumeProfileDefinition {
    public int Version { get; set; } = 1;
    public List<VolumeComponentDefinition> Components { get; set; } = new();
}

public sealed class VolumeComponentDefinition {
    public string Type { get; set; }
    public bool Active { get; set; } = true;
    public Dictionary<string, VolumeParameterDefinition> Parameters { get; set; } = new();
}

public sealed class VolumeParameterDefinition {
    public bool Overridden { get; set; }
    public JsonElement Value { get; set; }
}
