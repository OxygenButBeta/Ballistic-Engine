namespace BallisticEngine.Editor;

internal static class RemoteCommandQueue {
    sealed record Pending(Func<object> Work, TaskCompletionSource<object> Completion);

    static readonly object gate = new();
    static readonly Queue<Pending> pending = new();

    public static object Execute(Func<object> work, int timeoutMs = 30_000) {
        var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
            pending.Enqueue(new Pending(work, completion));
        if (!completion.Task.Wait(timeoutMs))
            throw new TimeoutException("the editor's main thread didn't process the command in time");
        return completion.Task.GetAwaiter().GetResult();
    }

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
