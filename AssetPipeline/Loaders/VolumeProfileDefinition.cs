using System.Text.Json;

namespace BallisticEngine.AssetPipeline.Loaders;

// On-disk shape of a `.volume` profile asset (JSON, like .mat). Components are keyed by
// their ComponentRegistry name; parameters by field name. Values are float/int/bool or a
// 3-element array for vectors/colors — VolumeProfileLoader converts both ways. Parameters
// missing from the file keep the component's compiled-in defaults, so old files survive
// new parameters.
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
