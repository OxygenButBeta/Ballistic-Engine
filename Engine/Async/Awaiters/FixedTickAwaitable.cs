using System.Runtime.CompilerServices;

namespace BallisticEngine;

public readonly struct FixedTickAwaitable {
    public FixedTickAwaiter GetAwaiter() => new();

    public readonly struct FixedTickAwaiter : INotifyCompletion {
        public bool IsCompleted => false;
        public void GetResult() { }
        public void OnCompleted(Action continuation) => CoroutineRunner.ScheduleFixed(continuation);
    }
}
