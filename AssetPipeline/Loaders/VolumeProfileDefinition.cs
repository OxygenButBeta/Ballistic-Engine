using System.Text.Json;

namespace BallisticEngine.AssetPipeline.Loaders;

public sealed class VolumeProfileDefinition {
    public int Version { get; set; } = 1;
    public List<VolumeComponentDefinition> Components { get; set; } = new();
}
