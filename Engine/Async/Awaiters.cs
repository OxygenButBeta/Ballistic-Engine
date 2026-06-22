using System.Runtime.CompilerServices;

namespace BallisticEngine;

public readonly struct NextFrameAwaitable {
    public NextFrameAwaiter GetAwaiter() => new();

    public readonly struct NextFrameAwaiter : INotifyCompletion {
        public bool IsCompleted => false;
        public void GetResult() { }
        public void OnCompleted(Action continuation) => CoroutineRunner.ScheduleNextFrame(continuation);
    }
}

public readonly struct FixedTickAwaitable {
    public FixedTickAwaiter GetAwaiter() => new();

    public readonly struct FixedTickAwaiter : INotifyCompletion {
        public bool IsCompleted => false;
        public void GetResult() { }
        public void OnCompleted(Action continuation) => CoroutineRunner.ScheduleFixed(continuation);
    }
}

public readonly struct EndOfFrameAwaitable {
    public EndOfFrameAwaiter GetAwaiter() => new();

    public readonly struct EndOfFrameAwaiter : INotifyCompletion {
        public bool IsCompleted => false;
        public void GetResult() { }
        public void OnCompleted(Action continuation) => CoroutineRunner.ScheduleEndOfFrame(continuation);
    }
}

public readonly struct DelayAwaitable {
    readonly float seconds;
    public DelayAwaitable(float seconds) => this.seconds = seconds;

    public DelayAwaiter GetAwaiter() => new(seconds);

    public struct DelayAwaiter : INotifyCompletion {
        float remaining;
        public DelayAwaiter(float seconds) => remaining = seconds;

        public readonly bool IsCompleted => remaining <= 0f;
        public readonly void GetResult() { }

        public void OnCompleted(Action continuation) {
            float local = remaining;
            CoroutineRunner.SchedulePoll(dt => (local -= dt) <= 0f, continuation);
        }
    }
}

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
