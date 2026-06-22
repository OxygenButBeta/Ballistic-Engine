using System.Text.Json;

namespace BallisticEngine.AssetPipeline.Loaders;

public sealed class VolumeComponentDefinition {
    public string Type { get; set; }
    public bool Active { get; set; } = true;
    public Dictionary<string, VolumeParameterDefinition> Parameters { get; set; } = new();
}
