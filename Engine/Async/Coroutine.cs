namespace BallisticEngine;

public static class Coroutine {
    public static NextFrameAwaitable NextFrame() => new();

    public static FixedTickAwaitable WaitForFixedTick() => new();

    public static EndOfFrameAwaitable EndOfFrame() => new();

    public static DelayAwaitable DelaySeconds(float seconds) => new(seconds);

    public static WaitUntilAwaitable WaitUntil(Func<bool> predicate) => new(predicate);
    public static WaitWhileAwaitable WaitWhile(Func<bool> predicate) => new(predicate);

    public static CoroutineHandle Run(IEnumerator<IYieldInstruction> routine) =>
        CoroutineRunner.Start(routine);

    public static void Stop(CoroutineHandle handle) => CoroutineRunner.Stop(handle);

    public static void RunNextFrame(Action action) {
        if (action is not null)
            CoroutineRunner.ScheduleNextFrame(action);
    }

    public static void RunAfter(float seconds, Action action) {
        if (action is null)
            return;
        if (seconds <= 0f) { CoroutineRunner.ScheduleNextFrame(action); return; }
        float remaining = seconds;
        CoroutineRunner.SchedulePoll(dt => (remaining -= dt) <= 0f, action);
    }

    public static void RunWhen(Func<bool> condition, Action action) {
        if (action is null || condition is null)
            return;
        CoroutineRunner.SchedulePoll(_ => condition(), action);
    }

    public static CoroutineHandle RunEveryFrame(Action perFrame, Func<bool> stop = null) =>
        Run(EveryFrame(perFrame, stop));

    static IEnumerator<IYieldInstruction> EveryFrame(Action perFrame, Func<bool> stop) {
        while (stop is null || !stop()) {
            perFrame?.Invoke();
            yield return null;
        }
    }

    public static async void RunTask(Task task) {
        try {
            await task;
        }
        catch (Exception e) {
            Debugging.LogError($"Async task faulted: {e}");
        }
    }

    internal static void Tick(float deltaTime) => CoroutineRunner.Tick(deltaTime);
    internal static void FixedTick(float fixedDelta) => CoroutineRunner.FixedTick(fixedDelta);
    public static void EndOfFramePump() => CoroutineRunner.EndOfFrame();
    internal static void Reset() => CoroutineRunner.Reset();
}
