namespace BallisticEngine;

// The main-thread pump that drives BOTH styles of async game code:
//   * classic IEnumerator coroutines (Coroutine.Run(IEnumerator) + WaitForSeconds/yield), and
//   * UniTask-style awaiters (await Coroutine.NextFrame()/DelaySeconds()/WaitUntil()), whose
//     continuations are queued here and resumed on the right frame phase.
//
// Everything runs on the MAIN GAME THREAD — no thread pool, no SynchronizationContext. That's the
// whole point: game code that awaits engine time resumes deterministically inside the frame, can
// touch transforms/components freely, and is firewalled by the same never-throw discipline as the
// rest of the engine. Pumped from SceneManager.Update (Tick phase) and Physics.Advance (fixed
// phase). Reset on play stop so coroutines don't survive into edit mode.
//
// Design credit: the awaiter/yield ergonomics mirror Cysharp UniTask (MIT). This is an independent
// engine-native implementation driven by Ballistic's own loop — no Unity PlayerLoop dependency.
public static class CoroutineRunner {
    // A live coroutine: the IEnumerator plus the instruction it's currently waiting on. Async-method
    // continuations register as a degenerate coroutine (a one-shot resume) via the awaiter queues
    // below instead of going through here.
    sealed class Routine {
        public IEnumerator<IYieldInstruction> Enumerator;
        public IYieldInstruction Current;
        public CoroutineHandle Handle;
        public bool Done;
    }

    static readonly List<Routine> routines = new();
    static readonly List<Routine> pendingAdd = new();

    // Continuations awaiting a specific resume point, drained on the matching phase. Separate lists
    // so a NextFrame await doesn't get resumed by the fixed-step pump and vice versa.
    static readonly List<Action> nextFramePump = new();
    static readonly List<Action> nextFrameStaging = new();
    static readonly List<Action> fixedPump = new();
    static readonly List<Action> fixedStaging = new();
    static readonly List<Action> endOfFramePump = new();
    static readonly List<Action> endOfFrameStaging = new();

    static ulong nextId = 1;

    // ---- Public scheduling surface (used by Coroutine facade + awaiters) --------------------

    public static CoroutineHandle Start(IEnumerator<IYieldInstruction> enumerator) {
        if (enumerator is null)
            return default;
        var routine = new Routine {
            Enumerator = enumerator,
            Handle = new CoroutineHandle(nextId++),
        };
        pendingAdd.Add(routine);
        return routine.Handle;
    }

    public static void Stop(CoroutineHandle handle) {
        if (!handle.IsValid)
            return;
        foreach (Routine r in routines)
            if (r.Handle.Id == handle.Id)
                r.Done = true;
        foreach (Routine r in pendingAdd)
            if (r.Handle.Id == handle.Id)
                r.Done = true;
    }

    // Awaiter hooks: register a continuation to resume on the next Tick / fixed step / end of frame.
    public static void ScheduleNextFrame(Action continuation) => nextFrameStaging.Add(continuation);
    public static void ScheduleFixed(Action continuation) => fixedStaging.Add(continuation);
    public static void ScheduleEndOfFrame(Action continuation) => endOfFrameStaging.Add(continuation);

    // A condition the runner polls each Tick; when `ready()` returns true the continuation fires and
    // the poll is removed. Backs await DelaySeconds/WaitUntil/WaitWhile — anything that needs to be
    // re-checked over multiple frames rather than resuming on a fixed phase.
    sealed class Poll {
        public Func<float, bool> Ready;   // (deltaTime) -> done?
        public Action Continuation;
        public bool Fired;
    }

    static readonly List<Poll> polls = new();
    static readonly List<Poll> pendingPolls = new();

    public static void SchedulePoll(Func<float, bool> ready, Action continuation) =>
        pendingPolls.Add(new Poll { Ready = ready, Continuation = continuation });

    // ---- Pumps (called by the engine loop) --------------------------------------------------

    // Variable-step pump: advances IEnumerator coroutines and resumes NextFrame/time/condition
    // awaiters. Called once per frame from SceneManager.Update, BEFORE scene Tick so a coroutine
    // started last frame and its awaiting code observe consistent per-frame state.
    public static void Tick(float deltaTime) {
        // Promote staged next-frame continuations and run them (a continuation may stage more for
        // the following frame — those wait, they don't run this pump).
        DrainStaging(nextFrameStaging, nextFramePump);
        RunQueue(nextFramePump);

        // Poll time/condition awaiters (await DelaySeconds/WaitUntil/...). A poll that fires runs its
        // continuation and drops out; a continuation may register more polls (they start next frame).
        if (pendingPolls.Count > 0) {
            polls.AddRange(pendingPolls);
            pendingPolls.Clear();
        }
        for (int i = 0; i < polls.Count; i++) {
            Poll poll = polls[i];
            if (poll.Fired)
                continue;
            bool ready;
            try { ready = poll.Ready is null || poll.Ready(deltaTime); }
            catch (Exception e) { Debugging.LogError($"Async wait predicate threw: {e}"); ready = true; }
            if (!ready)
                continue;
            poll.Fired = true;
            try { poll.Continuation?.Invoke(); }
            catch (Exception e) { Debugging.LogError($"Async continuation threw: {e}"); }
        }
        polls.RemoveAll(p => p.Fired);

        // Advance classic coroutines.
        if (pendingAdd.Count > 0) {
            routines.AddRange(pendingAdd);
            pendingAdd.Clear();
        }

        for (int i = 0; i < routines.Count; i++) {
            Routine routine = routines[i];
            if (routine.Done)
                continue;

            // Still waiting on a time/condition instruction? Tick it; only advance when ready.
            if (routine.Current is { } instruction && !instruction.IsReady(deltaTime, fixedStep: false))
                continue;

            AdvanceRoutine(routine, deltaTime);
        }

        routines.RemoveAll(r => r.Done);
    }

    // Fixed-step pump: resumes WaitForFixedTick awaiters and time-instructions flagged for fixed
    // stepping. Called from Physics.Advance before each step.
    public static void FixedTick(float fixedDelta) {
        DrainStaging(fixedStaging, fixedPump);
        RunQueue(fixedPump);

        // Classic coroutines waiting specifically on a fixed step advance here.
        for (int i = 0; i < routines.Count; i++) {
            Routine routine = routines[i];
            if (routine.Done || routine.Current is not { } instruction)
                continue;
            if (instruction.WaitsForFixed && instruction.IsReady(fixedDelta, fixedStep: true))
                AdvanceRoutine(routine, fixedDelta);
        }
    }

    // End-of-frame pump: resume EndOfFrame awaiters. Called by the host after the scene renders.
    public static void EndOfFrame() {
        DrainStaging(endOfFrameStaging, endOfFramePump);
        RunQueue(endOfFramePump);
    }

    // Play teardown: abandon every coroutine and pending continuation so nothing leaks into edit
    // mode or the next play session (mirrors how physics/renderer reset on StopPlay).
    public static void Reset() {
        routines.Clear();
        pendingAdd.Clear();
        polls.Clear(); pendingPolls.Clear();
        nextFramePump.Clear(); nextFrameStaging.Clear();
        fixedPump.Clear(); fixedStaging.Clear();
        endOfFramePump.Clear(); endOfFrameStaging.Clear();
    }

    // ---- Internals --------------------------------------------------------------------------

    static void AdvanceRoutine(Routine routine, float delta) {
        try {
            if (routine.Enumerator.MoveNext()) {
                routine.Current = routine.Enumerator.Current;
                // A freshly-yielded instruction primes its internal clock from this delta.
                routine.Current?.Prime(delta);
            }
            else {
                routine.Done = true;
            }
        }
        catch (Exception e) {
            // One throwing coroutine must not kill the pump (engine never-throw convention).
            Debugging.LogError($"Coroutine threw and was stopped: {e}");
            routine.Done = true;
        }
    }

    static void DrainStaging(List<Action> staging, List<Action> pump) {
        if (staging.Count == 0)
            return;
        pump.AddRange(staging);
        staging.Clear();
    }

    static void RunQueue(List<Action> pump) {
        if (pump.Count == 0)
            return;
        // Snapshot: a continuation may schedule another continuation; that one waits for the next
        // pump rather than running re-entrantly here.
        Action[] batch = pump.ToArray();
        pump.Clear();
        foreach (Action action in batch) {
            try { action(); }
            catch (Exception e) { Debugging.LogError($"Async continuation threw: {e}"); }
        }
    }
}

// Opaque handle to a started coroutine, for stopping it (Unity's Coroutine reference).
public readonly struct CoroutineHandle {
    public readonly ulong Id;
    public CoroutineHandle(ulong id) => Id = id;
    public bool IsValid => Id != 0;
}
