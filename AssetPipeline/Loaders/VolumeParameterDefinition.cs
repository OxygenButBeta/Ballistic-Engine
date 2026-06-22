using System.Text.Json;

namespace BallisticEngine.AssetPipeline.Loaders;

public sealed class VolumeParameterDefinition {
    public bool Overridden { get; set; }
    public JsonElement Value { get; set; }
}
