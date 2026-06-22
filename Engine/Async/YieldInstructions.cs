namespace BallisticEngine;

public interface IYieldInstruction {
    bool IsReady(float delta, bool fixedStep);

    bool WaitsForFixed { get; }

    void Prime(float delta);
}

public sealed class WaitForSeconds : IYieldInstruction {
    float remaining;

    public WaitForSeconds(float seconds) => remaining = seconds;

    public bool WaitsForFixed => false;

    public void Prime(float delta) { }

    public bool IsReady(float delta, bool fixedStep) {
        remaining -= delta;
        return remaining <= 0f;
    }
}

public sealed class WaitForFixedUpdate : IYieldInstruction {
    bool consumedFirst;

    public bool WaitsForFixed => true;
    public void Prime(float delta) { }

    public bool IsReady(float delta, bool fixedStep) {
        if (!consumedFirst) {
            consumedFirst = true;
            return true;
        }
        return true;
    }
}

public sealed class WaitUntil : IYieldInstruction {
    readonly Func<bool> predicate;
    public WaitUntil(Func<bool> predicate) => this.predicate = predicate;

    public bool WaitsForFixed => false;
    public void Prime(float delta) { }
    public bool IsReady(float delta, bool fixedStep) => predicate is null || predicate();
}

public sealed class WaitWhile : IYieldInstruction {
    readonly Func<bool> predicate;
    public WaitWhile(Func<bool> predicate) => this.predicate = predicate;

    public bool WaitsForFixed => false;
    public void Prime(float delta) { }
    public bool IsReady(float delta, bool fixedStep) => predicate is null || !predicate();
}

public sealed class WaitForNextFrame : IYieldInstruction {
    bool primed;
    public bool WaitsForFixed => false;
    public void Prime(float delta) => primed = true;
    public bool IsReady(float delta, bool fixedStep) => primed;
}
