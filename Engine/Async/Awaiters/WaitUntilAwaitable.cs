using System.Runtime.CompilerServices;

namespace BallisticEngine;

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
