using System.Text.Json;

namespace BallisticEngine.Cli;

internal static class Json {
    static readonly JsonSerializerOptions Options = new() {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Write(object value) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(value, Options));

    public static void WriteRaw(JsonElement element) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(element, Options));

    public static void WriteError(string message) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(new { error = message }, Options));
}
