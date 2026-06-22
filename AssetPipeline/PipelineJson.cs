using System.Text.Json;
using System.Text.Json.Serialization;

namespace BallisticEngine.AssetPipeline;

public static class PipelineJson {
    public static readonly JsonSerializerOptions Options = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static T Read<T>(string filePath) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(filePath), Options);

    public static void Write<T>(string filePath, T value) =>
        File.WriteAllText(filePath, JsonSerializer.Serialize(value, Options));
}
