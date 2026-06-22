namespace BallisticEngine;

public sealed class WaitForNextFrame : IYieldInstruction {
    bool primed;
    public bool WaitsForFixed => false;
    public void Prime(float delta) => primed = true;
    public bool IsReady(float delta, bool fixedStep) => primed;
}
