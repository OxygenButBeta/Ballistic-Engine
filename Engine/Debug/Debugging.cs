namespace BallisticEngine;

public static class Debugging
{
    public static ILogger Logger { get; internal set; }

    // Mirror of every log call, for tooling (the editor console subscribes).
    // Levels: 0 = info, 1 = warning, 2 = error.
    public static event Action<string, int> OnMessage;

    public static void Log(object message, BObject source = null)
    {
        Logger?.Log(message, source);
        OnMessage?.Invoke(message?.ToString() ?? string.Empty, 0);
    }

    public static void LogError(object message, BObject source = null)
    {
        Logger?.LogError(message, source);
        OnMessage?.Invoke(message?.ToString() ?? string.Empty, 2);
    }

    public static void LogWarning(object message, BObject source = null)
    {
        Logger?.LogWarning(message, source);
        OnMessage?.Invoke(message?.ToString() ?? string.Empty, 1);
    }

    const SystemLogPriority MinimumPriority = SystemLogPriority.Off;

    public static void SystemLog(object message, SystemLogPriority priority = SystemLogPriority.Low,
        BObject source = null)
    {
        if (priority < MinimumPriority)
            return;

        Log("[System Log] " + message, source);
    }
}
