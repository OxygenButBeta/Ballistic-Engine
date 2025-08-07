namespace BallisticEngine;

public static class Debugging
{
    public static ILogger Logger { get; internal set; }
    public static void Log(string message, BObject source = null) => Logger.Log(message, source);
    public static void LogError(string message, BObject source = null) => Logger.LogError(message, source);
    public static void LogWarning(string message, BObject source = null) => Logger.LogWarning(message, source);

    const SystemLogPriority MinimumPriority = SystemLogPriority.Critical;

    public static void SystemLog(string message, SystemLogPriority priority = SystemLogPriority.Low,
        BObject source = null)
    {
        if (priority < MinimumPriority)
            return;

        Log("[System Log] " + message, source);
    }
}