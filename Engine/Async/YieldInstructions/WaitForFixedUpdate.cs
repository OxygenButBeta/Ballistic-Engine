namespace BallisticEngine;

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
