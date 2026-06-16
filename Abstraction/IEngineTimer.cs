namespace BallisticEngine;

/// <summary>
/// This interface defines the methods and properties required for a timer in the Ballistic Engine.
/// </summary>
public interface IEngineTimer
{
    double DeltaTime { get; }
    double TotalTime { get; }

    // Advanced by the host's frame loop. Public (was internal-to-engine) so an out-of-assembly host —
    // the DX12 runtime in BallisticEngine.DX12 — can provide a timer; the GL/headless timers live in the
    // engine assembly and were fine with internal, but the DX12 host needs to implement it too.
    void Update(double deltaTime);
}