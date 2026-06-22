using System.Text;

namespace BallisticEngine;

public static class JsonlLog {
    static readonly object gate = new();
    static StreamWriter writer;

    public static void Start(string path) {
        lock (gate) {
            if (writer is not null)
                return;
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                writer = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch {
                return;
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
