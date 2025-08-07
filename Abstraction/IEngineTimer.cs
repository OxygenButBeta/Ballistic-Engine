namespace BallisticEngine;

/// <summary>
/// This interface defines the methods and properties required for a timer in the Ballistic Engine.
/// </summary>
public interface IEngineTimer
{
    double DeltaTime { get; }
    double TotalTime { get; }

    internal void Update(double  deltaTime);
}