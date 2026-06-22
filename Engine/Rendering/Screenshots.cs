namespace BallisticEngine;

public static class Screenshots {
    public sealed class Request {
        public required string Path;
        public int SettleFrames;
        public Action<string> OnSaved;
    }

    static readonly object gate = new();
    static readonly List<Request> pending = new();

    public static void Capture(string path, int settleFrames = 0, Action<string> onSaved = null) {
        if (string.IsNullOrWhiteSpace(path))
            return;
        lock (gate)
            pending.Add(new Request { Path = path, SettleFrames = settleFrames, OnSaved = onSaved });
    }

    public static List<Request> DueThisFrame() {
        lock (gate) {
            if (pending.Count == 0)
                return null;
            List<Request> due = null;
            for (int i = pending.Count - 1; i >= 0; i--) {
                if (pending[i].SettleFrames-- > 0)
                    continue;
                (due ??= new()).Add(pending[i]);
                pending.RemoveAt(i);
            }
            return due;
        }
    }
}
