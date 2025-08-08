namespace BallisticEngine;

public static class Debugging
{
    public static ILogger Logger { get; internal set; }
    public static void Log(object message, BObject source = null) => Logger.Log(message, source);
    public static void LogError(object message, BObject source = null) => Logger.LogError(message, source);
    public static void LogWarning(object message, BObject source = null) => Logger.LogWarning(message, source);

    const SystemLogPriority MinimumPriority = SystemLogPriority.Off;

    public static void SystemLog(object message, SystemLogPriority priority = SystemLogPriority.Low,
        BObject source = null)
    {
        if (priority < MinimumPriority)
            return;

        Log("[System Log] " + message, source);
    }
}