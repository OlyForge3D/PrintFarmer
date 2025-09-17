using System.Diagnostics;

namespace Farm.Web.Api.Services.Telemetry;

public interface IUnifiedLoggingService
{
    void LogDebug(string message, params object[] args);
    void LogInformation(string message, params object[] args);
    void LogWarning(string message, params object[] args);
    void LogError(string message, params object[] args);
    void LogError(Exception exception, string message, params object[] args);
    void LogCritical(string message, params object[] args);
    void LogCritical(Exception exception, string message, params object[] args);

    // Context-aware logging
    void LogWithContext(LogLevel level, string category, string message, object? context = null, Exception? exception = null);
}

public sealed class UnifiedLoggingService : IUnifiedLoggingService, IDisposable
{
    private readonly ILogger<UnifiedLoggingService> _logger;
    private readonly ActivitySource _activitySource;

    public UnifiedLoggingService(
        ILogger<UnifiedLoggingService> logger,
        IPrintFarmerTelemetryService telemetryService)
    {
        _logger = logger;
        _activitySource = new ActivitySource("PrintFarmer.Logging");
    }

    public void LogDebug(string message, params object[] args)
    {
        LogWithTelemetry(LogLevel.Debug, "Debug", message, args);
    }

    public void LogInformation(string message, params object[] args)
    {
        LogWithTelemetry(LogLevel.Information, "Information", message, args);
    }

    public void LogWarning(string message, params object[] args)
    {
        LogWithTelemetry(LogLevel.Warning, "Warning", message, args);
    }

    public void LogError(string message, params object[] args)
    {
        LogWithTelemetry(LogLevel.Error, "Error", message, args);
    }

    public void LogError(Exception exception, string message, params object[] args)
    {
        LogWithTelemetry(LogLevel.Error, "Error", message, args, exception);
    }

    public void LogCritical(string message, params object[] args)
    {
        LogWithTelemetry(LogLevel.Critical, "Critical", message, args);
    }

    public void LogCritical(Exception exception, string message, params object[] args)
    {
        LogWithTelemetry(LogLevel.Critical, "Critical", message, args, exception);
    }

    public void LogWithContext(LogLevel level, string category, string message, object? context = null, Exception? exception = null)
    {
        using Activity? activity = _activitySource.StartActivity($"Log.{category}");

        // Add context to telemetry
        if (activity != null)
        {
            activity.SetTag("log.level", level.ToString());
            activity.SetTag("log.category", category);
            activity.SetTag("log.message", message);

            if (context != null)
            {
                activity.SetTag("log.context", System.Text.Json.JsonSerializer.Serialize(context));
            }

            if (exception != null)
            {
                activity.SetTag("error", true);
                activity.SetTag("exception.type", exception.GetType().Name);
                activity.SetTag("exception.message", exception.Message);
                activity.SetTag("exception.stackTrace", exception.StackTrace);

                // Record exception in span
                activity.AddException(exception);
                activity.SetStatus(ActivityStatusCode.Error, exception.Message);
            }
        }

        // Log to structured logger
        if (exception != null)
        {
            _logger.Log(level, exception, "[{Category}] {Message} Context: {Context}",
                category, message, context ?? "None");
        }
        else
        {
            _logger.Log(level, "[{Category}] {Message} Context: {Context}",
                category, message, context ?? "None");
        }
    }

    private void LogWithTelemetry(LogLevel level, string category, string message, object[] args, Exception? exception = null)
    {
        using Activity? activity = _activitySource.StartActivity($"Log.{category}");

        string formattedMessage = args.Length > 0 ? string.Format(message, args) : message;

        if (activity != null)
        {
            activity.SetTag("log.level", level.ToString());
            activity.SetTag("log.category", category);
            activity.SetTag("log.message", formattedMessage);

            if (exception != null)
            {
                activity.SetTag("error", true);
                activity.SetTag("exception.type", exception.GetType().Name);
                activity.SetTag("exception.message", exception.Message);
                activity.AddException(exception);
                activity.SetStatus(ActivityStatusCode.Error, exception.Message);
            }
        }

        // Log to structured logger with telemetry context
        if (exception != null)
        {
            _logger.Log(level, exception, "[{Category}] {Message}", category, formattedMessage);
        }
        else
        {
            _logger.Log(level, "[{Category}] {Message}", category, formattedMessage);
        }
    }

    public void Dispose()
    {
        _activitySource?.Dispose();
    }
}

// Extension methods for easy integration
public static class UnifiedLoggingExtensions
{
    public static void LogPrinterOperation(this IUnifiedLoggingService logger, string operation, string printerId, bool success, string? details = null)
    {
        LogLevel level = success ? LogLevel.Information : LogLevel.Warning;
        string message = success ? "Printer operation completed successfully" : "Printer operation failed";

        logger.LogWithContext(level, "PrinterOperation", message, new
        {
            Operation = operation,
            PrinterId = printerId,
            Success = success,
            Details = details
        });
    }

    public static void LogSlicerOperation(this IUnifiedLoggingService logger, string operation, string engine, bool success, TimeSpan? duration = null, string? details = null)
    {
        LogLevel level = success ? LogLevel.Information : LogLevel.Error;
        string message = success ? "Slicer operation completed" : "Slicer operation failed";

        logger.LogWithContext(level, "SlicerOperation", message, new
        {
            Operation = operation,
            Engine = engine,
            Success = success,
            Duration = duration?.TotalSeconds,
            Details = details
        });
    }

    public static void LogFileOperation(this IUnifiedLoggingService logger, string operation, string fileName, bool success, long? fileSize = null, string? details = null)
    {
        LogLevel level = success ? LogLevel.Information : LogLevel.Warning;
        string message = success ? "File operation completed" : "File operation failed";

        logger.LogWithContext(level, "FileOperation", message, new
        {
            Operation = operation,
            FileName = fileName,
            Success = success,
            FileSize = fileSize,
            Details = details
        });
    }

    public static void LogApiRequest(this IUnifiedLoggingService logger, string method, string endpoint, int statusCode, TimeSpan duration, string? details = null)
    {
        LogLevel level = statusCode >= 400 ? LogLevel.Warning : LogLevel.Information;
        string message = $"API request processed";

        logger.LogWithContext(level, "ApiRequest", message, new
        {
            Method = method,
            Endpoint = endpoint,
            StatusCode = statusCode,
            Duration = duration.TotalMilliseconds,
            Details = details
        });
    }
}
