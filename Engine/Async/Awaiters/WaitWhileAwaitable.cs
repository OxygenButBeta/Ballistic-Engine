using System.Runtime.CompilerServices;

namespace BallisticEngine;

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
