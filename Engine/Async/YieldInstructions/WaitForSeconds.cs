namespace BallisticEngine;

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
