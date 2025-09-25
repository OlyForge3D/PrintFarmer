using System;

namespace Farm.Infrastructure;

public class UnifiedLoggingService : IUnifiedLoggingService
{
    public void LogInformation(string message)
    {
        // TODO: Integrate with telemetry if needed
        Console.WriteLine($"[INFO] {message}");
    }

    public void LogWarning(string message)
    {
        Console.WriteLine($"[WARN] {message}");
    }

    public void LogError(string message, Exception? ex = null)
    {
        Console.WriteLine($"[ERROR] {message}");
        if (ex != null)
        {
            Console.WriteLine($"Exception: {ex}");
        }
    }

    public void LogDebug(string message)
    {
        Console.WriteLine($"[DEBUG] {message}");
    }
}
