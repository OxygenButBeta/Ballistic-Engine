namespace BallisticEngine;

public sealed class WaitWhile : IYieldInstruction {
    readonly Func<bool> predicate;
    public WaitWhile(Func<bool> predicate) => this.predicate = predicate;

    public bool WaitsForFixed => false;
    public void Prime(float delta) { }
    public bool IsReady(float delta, bool fixedStep) => predicate is null || !predicate();
}
