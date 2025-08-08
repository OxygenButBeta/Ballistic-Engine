namespace BallisticEngine;

public interface ILogger
{
    void Log(object message, BObject source = null);
    void LogError(object message, BObject source = null);
    void LogWarning(object message, BObject source = null);
    
}