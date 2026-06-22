namespace BallisticEngine;

public interface IYieldInstruction {
    bool IsReady(float delta, bool fixedStep);

    bool WaitsForFixed { get; }

    void Prime(float delta);
}
