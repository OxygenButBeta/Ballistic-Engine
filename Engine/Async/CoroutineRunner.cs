namespace BallisticEngine;

public static class CoroutineRunner {
    sealed class Routine {
        public IEnumerator<IYieldInstruction> Enumerator;
        public IYieldInstruction Current;
        public CoroutineHandle Handle;
        public bool Done;
    }

    static readonly List<Routine> routines = new();
    static readonly List<Routine> pendingAdd = new();

    static readonly List<Action> nextFramePump = new();
    static readonly List<Action> nextFrameStaging = new();
    static readonly List<Action> fixedPump = new();
    static readonly List<Action> fixedStaging = new();
    static readonly List<Action> endOfFramePump = new();
    static readonly List<Action> endOfFrameStaging = new();

    static ulong nextId = 1;

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

    public static void ScheduleNextFrame(Action continuation) => nextFrameStaging.Add(continuation);
    public static void ScheduleFixed(Action continuation) => fixedStaging.Add(continuation);
    public static void ScheduleEndOfFrame(Action continuation) => endOfFrameStaging.Add(continuation);

    sealed class Poll {
        public Func<float, bool> Ready;
        public Action Continuation;
        public bool Fired;
    }

    static readonly List<Poll> polls = new();
    static readonly List<Poll> pendingPolls = new();

    public static void SchedulePoll(Func<float, bool> ready, Action continuation) =>
        pendingPolls.Add(new Poll { Ready = ready, Continuation = continuation });

    public static void Tick(float deltaTime) {
        DrainStaging(nextFrameStaging, nextFramePump);
        RunQueue(nextFramePump);

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

        if (pendingAdd.Count > 0) {
            routines.AddRange(pendingAdd);
            pendingAdd.Clear();
        }

        for (int i = 0; i < routines.Count; i++) {
            Routine routine = routines[i];
            if (routine.Done)
                continue;

            if (routine.Current is { } instruction && !instruction.IsReady(deltaTime, fixedStep: false))
                continue;

            AdvanceRoutine(routine, deltaTime);
        }

        routines.RemoveAll(r => r.Done);
    }

    public static void FixedTick(float fixedDelta) {
        DrainStaging(fixedStaging, fixedPump);
        RunQueue(fixedPump);

        for (int i = 0; i < routines.Count; i++) {
            Routine routine = routines[i];
            if (routine.Done || routine.Current is not { } instruction)
                continue;
            if (instruction.WaitsForFixed && instruction.IsReady(fixedDelta, fixedStep: true))
                AdvanceRoutine(routine, fixedDelta);
        }
    }

    public static void EndOfFrame() {
        DrainStaging(endOfFrameStaging, endOfFramePump);
        RunQueue(endOfFramePump);
    }

    public static void Reset() {
        routines.Clear();
        pendingAdd.Clear();
        polls.Clear(); pendingPolls.Clear();
        nextFramePump.Clear(); nextFrameStaging.Clear();
        fixedPump.Clear(); fixedStaging.Clear();
        endOfFramePump.Clear(); endOfFrameStaging.Clear();
    }

    static void AdvanceRoutine(Routine routine, float delta) {
        try {
            if (routine.Enumerator.MoveNext()) {
                routine.Current = routine.Enumerator.Current;
                routine.Current?.Prime(delta);
            }
            else {
                routine.Done = true;
            }
        }
        catch (Exception e) {
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
        Action[] batch = pump.ToArray();
        pump.Clear();
        foreach (Action action in batch) {
            try { action(); }
            catch (Exception e) { Debugging.LogError($"Async continuation threw: {e}"); }
        }
    }
}
