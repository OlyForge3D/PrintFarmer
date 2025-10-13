using System;
using Farm.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Telemetry;

// Extension methods for easy integration
public static class UnifiedLoggingExtensions
{
    public static void LogPrinterOperation(this IUnifiedLoggingService logger, string operation, string printerId, bool success, string? details = null)
    {
        LogLevel level = success ? LogLevel.Information : LogLevel.Warning;
        string message = success ? "Printer operation completed successfully" : "Printer operation failed";

        logger.LogWithContext(level, "PrinterOperation", message, null, null, new
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

        logger.LogWithContext(level, "SlicerOperation", message, null, null, new
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

        logger.LogWithContext(level, "FileOperation", message, null, null, new
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

        logger.LogWithContext(level, "ApiRequest", message, null, null, new
        {
            Method = method,
            Endpoint = endpoint,
            StatusCode = statusCode,
            Duration = duration.TotalMilliseconds,
            Details = details
        });
    }
}
