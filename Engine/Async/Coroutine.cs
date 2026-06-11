namespace BallisticEngine;

// The game-facing async facade — the one type game scripts reach for. Two interoperating styles:
//
//   1. UniTask-style async/await (preferred):
//          async void Spawn() {
//              await Coroutine.DelaySeconds(2f);
//              await Coroutine.WaitUntil(() => ready);
//              await Coroutine.NextFrame();
//          }
//      The awaits resume ON THE MAIN THREAD at the right frame phase (no thread pool).
//
//   2. Classic IEnumerator coroutines (Unity parity, drop-in for ported code):
//          IEnumerator<IYieldInstruction> Blink() {
//              while (true) { Flash(); yield return new WaitForSeconds(0.5f); }
//          }
//          Coroutine.Run(Blink());
//
// Plus ergonomic one-liners (RunNextFrame, RunAfter, RunEveryFrame, RunWhen) so the common cases
// don't need a whole method. Everything is pumped by CoroutineRunner from the engine loop and torn
// down on play stop.
public static class Coroutine {
    // ---- Awaiter factories (await these) ----------------------------------------------------

    // Resume next frame (Tick pump).
    public static NextFrameAwaitable NextFrame() => new();

    // Resume on the next fixed physics step.
    public static FixedTickAwaitable WaitForFixedTick() => new();

    // Resume after the scene renders this frame.
    public static EndOfFrameAwaitable EndOfFrame() => new();

    // Resume after `seconds` of game time.
    public static DelayAwaitable DelaySeconds(float seconds) => new(seconds);

    // Resume once the predicate becomes true / false.
    public static WaitUntilAwaitable WaitUntil(Func<bool> predicate) => new(predicate);
    public static WaitWhileAwaitable WaitWhile(Func<bool> predicate) => new(predicate);

    // ---- Classic coroutines -----------------------------------------------------------------

    // Starts an IEnumerator coroutine; returns a handle you can Stop. The coroutine yields
    // IYieldInstructions (WaitForSeconds, WaitUntil, ...) or null (= wait one frame).
    public static CoroutineHandle Run(IEnumerator<IYieldInstruction> routine) =>
        CoroutineRunner.Start(routine);

    public static void Stop(CoroutineHandle handle) => CoroutineRunner.Stop(handle);

    // ---- Ergonomic one-liners (the "makes things way easier" surface) -----------------------

    // Run an action on the NEXT frame (Unity's "do this after one frame" idiom, no coroutine).
    public static void RunNextFrame(Action action) {
        if (action is not null)
            CoroutineRunner.ScheduleNextFrame(action);
    }

    // Run an action after a delay (game-time seconds).
    public static void RunAfter(float seconds, Action action) {
        if (action is null)
            return;
        if (seconds <= 0f) { CoroutineRunner.ScheduleNextFrame(action); return; }
        float remaining = seconds;
        CoroutineRunner.SchedulePoll(dt => (remaining -= dt) <= 0f, action);
    }

    // Run an action once a condition first holds.
    public static void RunWhen(Func<bool> condition, Action action) {
        if (action is null || condition is null)
            return;
        CoroutineRunner.SchedulePoll(_ => condition(), action);
    }

    // Run an action every frame until `stop` returns true (or forever if null). Returns a handle to
    // cancel it early. Built on a classic coroutine so it tears down cleanly on play stop.
    public static CoroutineHandle RunEveryFrame(Action perFrame, Func<bool> stop = null) =>
        Run(EveryFrame(perFrame, stop));

    static IEnumerator<IYieldInstruction> EveryFrame(Action perFrame, Func<bool> stop) {
        while (stop is null || !stop()) {
            perFrame?.Invoke();
            yield return null; // wait one frame
        }
    }

    // Fire-and-forget an async method with engine-style exception logging (Unity's "async void but
    // safe"). Use when you don't need to await the result: Coroutine.RunTask(MyAsyncMethod());
    public static async void RunTask(Task task) {
        try {
            await task;
        }
        catch (Exception e) {
            Debugging.LogError($"Async task faulted: {e}");
        }
    }

    // ---- Engine/host plumbing (called by the engine loop and host exes; not for game code) ---
    // Public because the editor exe (a separate assembly) drives the end-of-frame pump itself.

    internal static void Tick(float deltaTime) => CoroutineRunner.Tick(deltaTime);
    internal static void FixedTick(float fixedDelta) => CoroutineRunner.FixedTick(fixedDelta);
    public static void EndOfFramePump() => CoroutineRunner.EndOfFrame();
    internal static void Reset() => CoroutineRunner.Reset();
}
