namespace BallisticEngine;

public interface IEngineTimer
{
    double DeltaTime { get; }
    double TotalTime { get; }

    internal void Update(double  deltaTime);
}