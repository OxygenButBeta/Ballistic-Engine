namespace BallisticEngine;

public interface ILogger
{
    void Log(string message, BObject source = null);
    void LogError(string message, BObject source = null);
    void LogWarning(string message, BObject source = null);
    
}