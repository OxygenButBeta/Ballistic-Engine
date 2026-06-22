using System.Runtime.CompilerServices;

namespace BallisticEngine;

public readonly struct EndOfFrameAwaitable {
    public EndOfFrameAwaiter GetAwaiter() => new();

    public readonly struct EndOfFrameAwaiter : INotifyCompletion {
        public bool IsCompleted => false;
        public void GetResult() { }
        public void OnCompleted(Action continuation) => CoroutineRunner.ScheduleEndOfFrame(continuation);
    }
}
