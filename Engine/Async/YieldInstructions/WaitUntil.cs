namespace BallisticEngine;

public sealed class WaitUntil : IYieldInstruction {
    readonly Func<bool> predicate;
    public WaitUntil(Func<bool> predicate) => this.predicate = predicate;

    public bool WaitsForFixed => false;
    public void Prime(float delta) { }
    public bool IsReady(float delta, bool fixedStep) => predicate is null || predicate();
}
