using System.Text.Json;

namespace BallisticEngine.Cli;

// Single JSON output point for the CLI, so every verb formats identically (indented, camelCase-free —
// member names are emitted exactly as given). Writes to stdout; errors use a {"error": "..."} shape.
internal static class Json {
    static readonly JsonSerializerOptions Options = new() {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // Serializes `value` to stdout as indented JSON.
    public static void Write(object value) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(value, Options));

    // Relays an already-parsed JSON element to stdout, re-indented (used to pass a subprocess's JSON result
    // through verbatim — e.g. `bal query` relaying the headless player's query output).
    public static void WriteRaw(JsonElement element) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(element, Options));

    // Emits a {"error": message} object on stdout (so a stdout-parsing caller still gets structured
    // output on failure). Program also prints a human line to stderr and returns exit code 1.
    public static void WriteError(string message) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(new { error = message }, Options));
}
