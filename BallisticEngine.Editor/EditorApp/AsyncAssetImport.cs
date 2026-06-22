using System.Diagnostics;

namespace BallisticEngine.Editor;

internal static class AsyncAssetImport {
    static readonly object gate = new();
    static Task running;
    static bool rerunQueued;
    static bool rerunForceAll;
    static Action queuedOnFinished;
    static Action pendingMainThreadCallback;
    static volatile bool busy;
    static volatile string status = "Importing...";
    static volatile string currentFile;
    static int completed, total;

    public static event Action AfterRefresh;

    public static bool IsBusy => busy;
    public static string Status => status;

    public static string CurrentFile => currentFile;

    public static float Fraction {
        get {
            int t = Volatile.Read(ref total);
            return t > 0 ? Math.Clamp((float)Volatile.Read(ref completed) / t, 0f, 1f) : -1f;
        }
    }

    public static void Request(string statusText = "Importing assets...", Action onFinished = null,
        bool forceAll = false) {
        lock (gate) {
            if (running is not null) {
                rerunQueued = true;
                rerunForceAll |= forceAll;
                queuedOnFinished = Combine(queuedOnFinished, onFinished);
                status = statusText;
                return;
            }

            Start(statusText, onFinished, forceAll);
        }
    }

    static void Start(string statusText, Action onFinished, bool forceAll = false) {
        busy = true;
        status = statusText;
        currentFile = null;
        Volatile.Write(ref completed, 0);
        Volatile.Write(ref total, 0);
        running = Task.Run(() => {
            var stopwatch = Stopwatch.StartNew();
            try {
                AssetDatabase.ImportProgress = file => currentFile = file;
                AssetDatabase.ImportProgressCount = (c, t) => {
                    Volatile.Write(ref total, t);
                    Volatile.Write(ref completed, c);
                };
                AssetDatabase.Refresh(forceAll);
            }
            catch (Exception exception) {
                Debugging.LogError($"Asset refresh failed: {exception.Message}");
            }
            finally {
                AssetChangeWatch.Snapshot();
                AssetDatabase.ImportProgress = null;
                AssetDatabase.ImportProgressCount = null;
                currentFile = null;
                OnRefreshComplete(onFinished, stopwatch.ElapsedMilliseconds);
            }
        });
    }

    static void OnRefreshComplete(Action onFinished, long elapsedMs) {
        lock (gate) {
            running = null;
            pendingMainThreadCallback = Combine(pendingMainThreadCallback, onFinished);

            if (rerunQueued) {
                rerunQueued = false;
                var next = queuedOnFinished;
                var force = rerunForceAll;
                queuedOnFinished = null;
                rerunForceAll = false;
                Start(status, next, force);
                return;
            }

            busy = false;
        }
    }

    public static void PumpCompletion() {
        Action callback;
        lock (gate) {
            callback = pendingMainThreadCallback;
            pendingMainThreadCallback = null;
        }

        if (callback is null)
            return;

        try {
            callback();
        }
        catch (Exception exception) {
            Debugging.LogError($"Post-import refresh step failed: {exception.Message}");
        }

        try {
            AfterRefresh?.Invoke();
        }
        catch (Exception exception) {
            Debugging.LogError($"Post-import AfterRefresh step failed: {exception.Message}");
        }
    }

    static Action Combine(Action a, Action b) =>
        a is null ? b : b is null ? a : a + b;
}
