using System.Diagnostics;

namespace BallisticEngine.Editor;

// Runs AssetDatabase.Refresh() off the render thread so dropping a big model doesn't freeze the
// editor window (Windows "Not Responding"). The import pipeline is pure CPU + file I/O (no GL),
// so it's safe to run on a Task; the GPU upload still happens lazily on the main thread later in
// AssetDatabase.Load. While a refresh is in flight IsBusy is true and the editor draws a modal
// busy overlay instead of accepting input.
//
// Completion work that must touch GL or the asset DB (thumbnail invalidation, Invalidate(guid))
// is handed in as an onFinished callback and run on the main thread from PumpCompletion(), which
// the editor calls once per frame. Refresh requests that arrive while one is running are coalesced
// into a single trailing re-run so rapid drops/edits don't stack up.
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

    public static bool IsBusy => busy;
    public static string Status => status;

    // The asset currently being processed (null between assets / before the first), for the overlay.
    public static string CurrentFile => currentFile;

    // Kicks off (or queues) a background refresh. onFinished, if supplied, runs ON THE MAIN THREAD
    // after the refresh completes — use it for thumbnail invalidation, Invalidate(guid), etc.
    // statusText is shown in the busy overlay. forceAll reimports everything (slow), ignoring
    // the pipeline's up-to-date checks.
    public static void Request(string statusText = "Importing assets...", Action onFinished = null,
        bool forceAll = false) {
        lock (gate) {
            if (running is not null) {
                // A refresh is already running; remember that we need another pass afterward and
                // chain the callbacks so none are lost. A queued force request keeps its force.
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
        running = Task.Run(() => {
            var stopwatch = Stopwatch.StartNew();
            try {
                AssetDatabase.ImportProgress = file => currentFile = file;
                AssetDatabase.Refresh(forceAll);
            }
            catch (Exception exception) {
                // The pipeline itself logs per-asset failures and never throws, but guard anyway so
                // a background exception can't take the editor down silently.
                Debugging.LogError($"Asset refresh failed: {exception.Message}");
            }
            finally {
                // Re-baseline the external-change fingerprint AFTER the refresh so files the
                // import generated don't look like outside edits on the next focus regain.
                AssetChangeWatch.Snapshot();
                AssetDatabase.ImportProgress = null;
                currentFile = null;
                OnRefreshComplete(onFinished, stopwatch.ElapsedMilliseconds);
            }
        });
    }

    static void OnRefreshComplete(Action onFinished, long elapsedMs) {
        lock (gate) {
            running = null;
            // Defer the user callback to the main thread; the import ran on a worker.
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

    // Called once per frame on the render thread. Runs any completion callbacks produced by a
    // finished background refresh (thumbnail invalidation, asset cache invalidation, etc.).
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
    }

    static Action Combine(Action a, Action b) =>
        a is null ? b : b is null ? a : a + b;
}
