namespace BallisticEngine.Editor;

// Main-thread executor for remote commands: the pipe thread queues work and BLOCKS for the
// result; EditorApplication pumps the queue once per frame (same lock-and-drain pattern as
// AsyncAssetImport, between PumpCompletion and BuildUI). Editor/engine state is only ever
// touched on the main thread — the protocol thread never calls engine code directly.
internal static class RemoteCommandQueue {
    sealed record Pending(Func<object> Work, TaskCompletionSource<object> Completion);

    static readonly object gate = new();
    static readonly Queue<Pending> pending = new();

    // Pipe thread: queue work, wait for the main thread to run it. Throws the handler's own
    // exception on failure, TimeoutException when the editor's main thread is stuck (modal
    // dialog, heavy synchronous import).
    public static object Execute(Func<object> work, int timeoutMs = 30_000) {
        var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
            pending.Enqueue(new Pending(work, completion));
        if (!completion.Task.Wait(timeoutMs))
            throw new TimeoutException("the editor's main thread didn't process the command in time");
        return completion.Task.GetAwaiter().GetResult();
    }

    // Main thread, once per frame.
    public static void Pump() {
        while (true) {
            Pending item;
            lock (gate) {
                if (pending.Count == 0)
                    return;
                item = pending.Dequeue();
            }
            try { item.Completion.SetResult(item.Work()); }
            catch (Exception ex) { item.Completion.SetException(ex); }
        }
    }
}
