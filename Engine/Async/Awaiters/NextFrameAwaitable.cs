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
