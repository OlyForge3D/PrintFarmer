namespace Farm.Infrastructure;

public interface IUnifiedLoggingService
{
    void LogInformation(string message);
    void LogWarning(string message);
    void LogError(string message, Exception? ex = null);
    void LogDebug(string message);
    // Add more methods as needed for telemetry, structured logging, etc.
}
