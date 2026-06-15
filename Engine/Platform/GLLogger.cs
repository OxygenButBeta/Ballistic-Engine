namespace BallisticEngine.GLImplementation;

public class GLLogger : ILogger
{
    //TODO : Implement a proper logging system with levels and output options
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