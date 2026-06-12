using System.Text;

namespace BallisticEngine;

// Machine-readable log mirror: every Debugging message also lands in a JSONL file (one
// {"t","level","msg"} object per line, truncated per session) so an external agent can tail
// structured logs instead of scraping console formats. Wired by EngineBootstrap to
// Library/Logs/engine.jsonl for editable projects (a shipped player must not write into its
// install folder). Thread-safe: import workers and script threads log too.
public static class JsonlLog {
    static readonly object gate = new();
    static StreamWriter writer;

    public static void Start(string path) {
        lock (gate) {
            if (writer is not null)
                return; // one sink per process
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                writer = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch {
                return; // logging must never take the engine down
            }
        }
        Debugging.OnMessage += Write;
    }

    static void Write(string message, int level) {
        var sb = new StringBuilder(message.Length + 64);
        sb.Append("{\"t\":\"").Append(DateTime.Now.ToString("HH:mm:ss.fff"))
          .Append("\",\"level\":\"").Append(level switch { 2 => "error", 1 => "warning", _ => "info" })
          .Append("\",\"msg\":\"");
        foreach (char c in message) {
            if (c is '"' or '\\') sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') { }
            else if (c < ' ') sb.Append(' ');
            else sb.Append(c);
        }
        sb.Append("\"}");
        lock (gate)
            writer?.WriteLine(sb.ToString());
    }
}
