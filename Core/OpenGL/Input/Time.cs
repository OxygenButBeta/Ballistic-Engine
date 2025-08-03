using OpenTK.Windowing.Desktop;

namespace BallisticEngine.Core.GL;

public class Time : IEngineTimer
{
    public double DeltaTime { get; private set; }
    public double TotalTime { get; private set; }

    public void Update(double deltaTime)
    {
        DeltaTime = deltaTime;
        TotalTime += deltaTime;
    }
}