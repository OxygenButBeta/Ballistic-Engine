using System.Runtime.CompilerServices;

namespace BallisticEngine;

// UniTask-style awaiters: the structs returned by Coroutine.NextFrame()/DelaySeconds()/WaitUntil()
// etc. so game code can `await` engine time inside an ordinary async method and resume ON THE MAIN
// THREAD at the right frame phase. Each awaiter registers its continuation with the CoroutineRunner
// — no thread pool, no SynchronizationContext. (Design ergonomics credit: Cysharp UniTask, MIT.)
//
// Pattern: GetAwaiter() returns the awaiter; IsCompleted=false forces OnCompleted to be called with
// the state-machine continuation, which we hand to the runner. The runner invokes it next
// frame / next fixed step / when the condition holds.

// await Coroutine.NextFrame() — resumes on the next Tick pump.
public readonly struct NextFrameAwaitable {
    public NextFrameAwaiter GetAwaiter() => new();

    public readonly struct NextFrameAwaiter : INotifyCompletion {
        public bool IsCompleted => false;
        public void GetResult() { }
        public void OnCompleted(Action continuation) => CoroutineRunner.ScheduleNextFrame(continuation);
    }
}

// await Coroutine.WaitForFixedTick() — resumes on the next fixed physics step.
public readonly struct FixedTickAwaitable {
    public FixedTickAwaiter GetAwaiter() => new();

    public readonly struct FixedTickAwaiter : INotifyCompletion {
        public bool IsCompleted => false;
        public void GetResult() { }
        public void OnCompleted(Action continuation) => CoroutineRunner.ScheduleFixed(continuation);
    }
}

// await Coroutine.EndOfFrame() — resumes after the scene renders this frame.
public readonly struct EndOfFrameAwaitable {
    public EndOfFrameAwaiter GetAwaiter() => new();

    public readonly struct EndOfFrameAwaiter : INotifyCompletion {
        public bool IsCompleted => false;
        public void GetResult() { }
        public void OnCompleted(Action continuation) => CoroutineRunner.ScheduleEndOfFrame(continuation);
    }
}

// await Coroutine.DelaySeconds(t) — resumes once `t` seconds of game time elapse. Counts down with
// the frame delta (so it pauses when the game pauses, like Unity's WaitForSeconds).
public readonly struct DelayAwaitable {
    readonly float seconds;
    public DelayAwaitable(float seconds) => this.seconds = seconds;

    public DelayAwaiter GetAwaiter() => new(seconds);

    public struct DelayAwaiter : INotifyCompletion {
        float remaining;
        public DelayAwaiter(float seconds) => remaining = seconds;

        // Already elapsed (or non-positive delay) completes synchronously — no frame wasted.
        public readonly bool IsCompleted => remaining <= 0f;
        public readonly void GetResult() { }

        public void OnCompleted(Action continuation) {
            float local = remaining;
            CoroutineRunner.SchedulePoll(dt => (local -= dt) <= 0f, continuation);
        }
    }
}

// await Coroutine.WaitUntil(pred) — resumes when the predicate first returns true.
public readonly struct WaitUntilAwaitable {
    readonly Func<bool> predicate;
    public WaitUntilAwaitable(Func<bool> predicate) => this.predicate = predicate;

    public WaitUntilAwaiter GetAwaiter() => new(predicate);

    public readonly struct WaitUntilAwaiter : INotifyCompletion {
        readonly Func<bool> predicate;
        public WaitUntilAwaiter(Func<bool> predicate) => this.predicate = predicate;

        public bool IsCompleted => predicate is null || predicate();
        public void GetResult() { }
        public void OnCompleted(Action continuation) {
            Func<bool> p = predicate;
            CoroutineRunner.SchedulePoll(_ => p is null || p(), continuation);
        }
    }
}

// await Coroutine.WaitWhile(pred) — resumes when the predicate first returns false.
public readonly struct WaitWhileAwaitable {
    readonly Func<bool> predicate;
    public WaitWhileAwaitable(Func<bool> predicate) => this.predicate = predicate;

    public WaitWhileAwaiter GetAwaiter() => new(predicate);

    public readonly struct WaitWhileAwaiter : INotifyCompletion {
        readonly Func<bool> predicate;
        public WaitWhileAwaiter(Func<bool> predicate) => this.predicate = predicate;

        public bool IsCompleted => predicate is not null && !predicate();
        public void GetResult() { }
        public void OnCompleted(Action continuation) {
            Func<bool> p = predicate;
            CoroutineRunner.SchedulePoll(_ => p is null || !p(), continuation);
        }
    }
}
