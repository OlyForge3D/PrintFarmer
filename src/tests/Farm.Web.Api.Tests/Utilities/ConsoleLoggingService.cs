using Farm.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Tests.Utilities;

/// <summary>
/// Simple console-based logging service for tests that actually outputs to console
/// instead of being suppressed like a Mock logger
/// </summary>
public class ConsoleLoggingService : IUnifiedLoggingService
{
    private readonly string _categoryName;

    public ConsoleLoggingService(string categoryName = "Test")
    {
        _categoryName = categoryName;
    }

    public void LogDebug(string message, string? correlationId = null, object? metadata = null)
    {
        Console.WriteLine($"[DEBUG] [{_categoryName}] {message}");
        if (metadata != null)
        {
            Console.WriteLine($"  Metadata: {metadata}");
        }
    }

    public void LogDebug(Exception exception, string message, string? correlationId = null, object? metadata = null)
    {
        Console.WriteLine($"[DEBUG] [{_categoryName}] {message}");
        Console.WriteLine($"  Exception: {exception.Message}");
        if (metadata != null)
        {
            Console.WriteLine($"  Metadata: {metadata}");
        }
    }

    public void LogInformation(string message, string? correlationId = null, object? metadata = null)
    {
        Console.WriteLine($"[INFO] [{_categoryName}] {message}");
        if (metadata != null)
        {
            Console.WriteLine($"  Metadata: {metadata}");
        }
    }

    public void LogWarning(string message, string? correlationId = null, object? metadata = null)
    {
        Console.WriteLine($"[WARN] [{_categoryName}] {message}");
        if (metadata != null)
        {
            Console.WriteLine($"  Metadata: {metadata}");
        }
    }

    public void LogWarning(Exception exception, string message, string? correlationId = null, object? metadata = null)
    {
        Console.WriteLine($"[WARN] [{_categoryName}] {message}");
        Console.WriteLine($"  Exception: {exception.Message}");
        if (metadata != null)
        {
            Console.WriteLine($"  Metadata: {metadata}");
        }
    }

    public void LogError(string message, string? correlationId = null, object? metadata = null)
    {
        Console.WriteLine($"[ERROR] [{_categoryName}] {message}");
        if (metadata != null)
        {
            Console.WriteLine($"  Metadata: {metadata}");
        }
    }

    public void LogError(Exception exception, string message, string? correlationId = null, object? metadata = null)
    {
        Console.WriteLine($"[ERROR] [{_categoryName}] {message}");
        Console.WriteLine($"  Exception: {exception.Message}");
        if (metadata != null)
        {
            Console.WriteLine($"  Metadata: {metadata}");
        }
    }

    public void LogCritical(string message, string? correlationId = null, object? metadata = null)
    {
        Console.WriteLine($"[CRITICAL] [{_categoryName}] {message}");
        if (metadata != null)
        {
            Console.WriteLine($"  Metadata: {metadata}");
        }
    }

    public void LogCritical(Exception exception, string message, string? correlationId = null, object? metadata = null)
    {
        Console.WriteLine($"[CRITICAL] [{_categoryName}] {message}");
        Console.WriteLine($"  Exception: {exception.Message}");
        if (metadata != null)
        {
            Console.WriteLine($"  Metadata: {metadata}");
        }
    }

    public void LogWithContext(LogLevel level, string category, string message, string? correlationId = null, object? metadata = null, object? context = null, Exception? exception = null)
    {
        var levelStr = level switch
        {
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "CRITICAL",
            _ => level.ToString().ToUpper()
        };

        Console.WriteLine($"[{levelStr}] [{_categoryName}:{category}] {message}");
        if (metadata != null)
        {
            Console.WriteLine($"  Metadata: {metadata}");
        }

        if (context != null)
        {
            Console.WriteLine($"  Context: {context}");
        }

        if (exception != null)
        {
            Console.WriteLine($"  Exception: {exception.Message}");
        }
    }
}
