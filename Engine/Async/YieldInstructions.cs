namespace BallisticEngine;

// Yield instructions for classic IEnumerator coroutines (Coroutine.Run). A coroutine `yield return`s
// one of these to pause until its condition is met; the CoroutineRunner ticks IsReady each pump and
// resumes when it returns true. `yield return null` (a bare null) means "wait one frame" — the
// runner treats a null Current as immediately-ready-next-frame.
//
// These mirror Unity's WaitForSeconds / WaitForFixedUpdate / WaitUntil / WaitWhile so ported
// coroutine code drops in unchanged.
public interface IYieldInstruction {
    // Called every matching pump with the elapsed delta. Return true when the coroutine may resume.
    bool IsReady(float delta, bool fixedStep);

    // True if this instruction wants to be advanced by the FIXED-step pump (WaitForFixedUpdate).
    bool WaitsForFixed { get; }

    // Called once right after the instruction is yielded, with the current delta — lets a timer
    // decide whether to count this same frame. Default: no-op.
    void Prime(float delta);
}

// Waits a real-time number of seconds (scaled by frame delta, like Unity's WaitForSeconds — it
// honors Time, not wall clock). Counts down across variable-step pumps.
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

// Resumes on the next fixed physics step (Unity's WaitForFixedUpdate).
public sealed class WaitForFixedUpdate : IYieldInstruction {
    bool consumedFirst;

    public bool WaitsForFixed => true;
    public void Prime(float delta) { }

    public bool IsReady(float delta, bool fixedStep) {
        // The first fixed pump after yielding resumes it.
        if (!consumedFirst) {
            consumedFirst = true;
            return true;
        }
        return true;
    }
}

// Resumes once the predicate returns true (Unity's WaitUntil). Evaluated each variable-step pump.
public sealed class WaitUntil : IYieldInstruction {
    readonly Func<bool> predicate;
    public WaitUntil(Func<bool> predicate) => this.predicate = predicate;

    public bool WaitsForFixed => false;
    public void Prime(float delta) { }
    public bool IsReady(float delta, bool fixedStep) => predicate is null || predicate();
}

// Resumes once the predicate returns false (Unity's WaitWhile).
public sealed class WaitWhile : IYieldInstruction {
    readonly Func<bool> predicate;
    public WaitWhile(Func<bool> predicate) => this.predicate = predicate;

    public bool WaitsForFixed => false;
    public void Prime(float delta) { }
    public bool IsReady(float delta, bool fixedStep) => predicate is null || !predicate();
}

// Resumes next frame (the explicit form of `yield return null`).
public sealed class WaitForNextFrame : IYieldInstruction {
    bool primed;
    public bool WaitsForFixed => false;
    public void Prime(float delta) => primed = true;
    public bool IsReady(float delta, bool fixedStep) => primed; // ready on the frame after the yield
}
