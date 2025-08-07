namespace BallisticEngine.GLImplementation;

public class GLLogger : ILogger
{
    //TODO : Implement a proper logging system with levels and output options
    public void Log(string message, BObject source = null)
    {
        if (source != null) Console.WriteLine($"[GLLogger] [{source.Name}] " + message);
        else Console.WriteLine($"[GLLogger] " + message);
    }

    public void LogError(string message, BObject source = null)
    {
        Log(message, source);
    }

    public void LogWarning(string message, BObject source = null)
    {
        Log(message, source);
    }
}