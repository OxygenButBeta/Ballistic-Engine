namespace BallisticEngine;

// On-demand backbuffer captures: anything engine-side (scripts, the editor command port, env-var
// harness) queues a request; the window backend drains due requests right before presenting and
// writes the image plus a .stats.json sidecar. Unlike the original BALLISTIC_SCREENSHOT one-shot,
// requests do NOT exit the process — callers that want run-and-exit do it in their callback.
public static class Screenshots {
    public sealed class Request {
        public required string Path;
        public int SettleFrames;            // presented frames to wait before capturing (TAA/streaming)
        public Action<string> OnSaved;      // fired on the render thread after the file is written
    }

    static readonly object gate = new();
    static readonly List<Request> pending = new();

    // Queue a capture. settleFrames counts presented frames before the capture happens (0 = the
    // next presented frame); use ~3+ to let TAA re-converge after a scene/camera change.
    public static void Capture(string path, int settleFrames = 0, Action<string> onSaved = null) {
        if (string.IsNullOrWhiteSpace(path))
            return;
        lock (gate)
            pending.Add(new Request { Path = path, SettleFrames = settleFrames, OnSaved = onSaved });
    }

    // Called by the window backend once per presented frame. Counts down settle frames and returns
    // the requests due THIS frame (or null — the common case — with zero allocation).
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
