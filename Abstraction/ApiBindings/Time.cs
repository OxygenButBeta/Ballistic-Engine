using BallisticEngine;

public static class Time
{
    internal static IEngineTimer Timer;
    public static double DeltaTime => Timer.DeltaTime;
    public static double TotalTime => Timer.TotalTime;
}