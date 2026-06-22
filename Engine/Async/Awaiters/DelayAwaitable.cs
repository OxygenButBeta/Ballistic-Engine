using System.Runtime.CompilerServices;

namespace BallisticEngine;

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
