using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace BallisticEngine.Editor;

internal static class RemotePort {
    public const string PipeName = "BallisticEditor";
    const int MaxConcurrentClients = 8;

    static CancellationTokenSource? cancel;

    public static void Start(EditorState state, EngineBootstrap bootstrap) {
        RemoteHandlers.Install(state, bootstrap);
        cancel = new CancellationTokenSource();
        var thread = new Thread(() => AcceptLoop(cancel.Token)) { IsBackground = true, Name = "RemotePort" };
        thread.Start();
        Debugging.Log($@"Remote command port listening on \\.\pipe\{PipeName} (up to {MaxConcurrentClients} clients)");
    }

    public static void Stop() => cancel?.Cancel();

    static void AcceptLoop(CancellationToken token) {
        while (!token.IsCancellationRequested) {
            NamedPipeServerStream? pipe = null;
            try {
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, MaxConcurrentClients,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                pipe.WaitForConnectionAsync(token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) {
                pipe?.Dispose();
                return;
            }
            catch (Exception) {
                pipe?.Dispose();
                continue;
            }

            var clientThread = new Thread(() => {
                try { Serve(pipe); }
                catch {
                }
                finally { pipe.Dispose(); }
            }) { IsBackground = true, Name = "RemotePort.Client" };
            clientThread.Start();
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
