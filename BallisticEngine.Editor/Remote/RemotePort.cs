using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace BallisticEngine.Editor;

// The editor's remote-control surface: a named-pipe server speaking newline-delimited JSON —
//   request:  {"id": 1, "method": "entity.create", "params": {"name": "Lamp"}}
//   response: {"id": 1, "result": {...}}  or  {"id": 1, "error": "..."}
// One client at a time; commands execute on the editor main thread between frames (see
// RemoteCommandQueue). The server thread lives in ENGINE code outside the script ALC, so it
// survives script hot-reloads and play-mode transitions — the documented failure mode of every
// in-editor bridge in other engines. The MCP server (separate process) is a thin client of this.
internal static class RemotePort {
    public const string PipeName = "BallisticEditor";

    static CancellationTokenSource? cancel;

    public static void Start(EditorState state, EngineBootstrap bootstrap) {
        RemoteHandlers.Install(state, bootstrap);
        cancel = new CancellationTokenSource();
        var thread = new Thread(() => ServerLoop(cancel.Token)) { IsBackground = true, Name = "RemotePort" };
        thread.Start();
        Debugging.Log($@"Remote command port listening on \\.\pipe\{PipeName}");
    }

    public static void Stop() => cancel?.Cancel();

    static void ServerLoop(CancellationToken token) {
        while (!token.IsCancellationRequested) {
            try {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                pipe.WaitForConnectionAsync(token).GetAwaiter().GetResult();
                Serve(pipe);
            }
            catch (OperationCanceledException) {
                return;
            }
            catch (Exception) {
                // Client dropped mid-handshake or pipe hiccup — accept the next connection.
            }
        }
    }

    static void Serve(NamedPipeServerStream pipe) {
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 16 * 1024, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 16 * 1024, leaveOpen: true) { AutoFlush = true };

        while (pipe.IsConnected) {
            string? line;
            try { line = reader.ReadLine(); }
            catch { return; }
            if (line is null)
                return;
            if (line.Length == 0)
                continue;

            long id = 0;
            object response;
            try {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                id = root.TryGetProperty("id", out JsonElement idEl) ? idEl.GetInt64() : 0;
                string method = root.TryGetProperty("method", out JsonElement m)
                    ? m.GetString() ?? "" : throw new Exception("missing 'method'");
                JsonElement parameters = root.TryGetProperty("params", out JsonElement p) ? p : default;

                // Blocks until the editor main thread executed the handler (JsonDocument stays
                // alive through the call because Execute is synchronous).
                object result = RemoteCommandQueue.Execute(() => RemoteHandlers.Dispatch(method, parameters));
                response = new { id, result };
            }
            catch (Exception ex) {
                Exception inner = ex is AggregateException { InnerException: { } i } ? i : ex;
                response = new { id, error = inner.Message };
            }

            try { writer.WriteLine(JsonSerializer.Serialize(response)); }
            catch { return; }
        }
    }
}
