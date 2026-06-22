namespace BallisticEngine.GLImplementation;

public class GLLogger : ILogger
{
    public void Log(object message, BObject source = null)
    {
        if (source != null) Console.WriteLine($"[GLLogger] [{source.Name}] " + message);
        else Console.WriteLine($"[GLLogger] " + message);
    }

    public void LogError(object message, BObject source = null)
    {
        Log(message, source);
    }

    public void LogWarning(object message, BObject source = null)
    {
        Log(message, source);
    }
}