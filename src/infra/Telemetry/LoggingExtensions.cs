using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Telemetry;

/// <summary>
/// Extension methods for IUnifiedLoggingService that automatically capture the calling type/method
/// </summary>
public static class LoggingExtensions
{
    /// <summary>
    /// Log a debug message with automatic caller information
    /// </summary>
    /// <param name="logger">The unified logging service instance.</param>
    /// <param name="message">The log message.</param>
    /// <param name="correlationId">Optional correlation ID for tracing.</param>
    /// <param name="metadata">Optional metadata to include with the log entry.</param>
    /// <param name="callerMemberName">Automatically captured caller member name.</param>
    /// <param name="callerFilePath">Automatically captured caller file path.</param>
    public static void LogDebugWithSource(
        this IUnifiedLoggingService logger,
        string message,
        string? correlationId = null,
        object? metadata = null,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "")
    {
        string sourceContext = FormatSourceContext(callerFilePath, callerMemberName);
        string enrichedMessage = $"[{sourceContext}] {message}";
        logger.LogDebug(enrichedMessage, correlationId, metadata);
    }

    /// <summary>
    /// Log a debug message with exception and automatic caller information
    /// </summary>
    /// <param name="logger">The unified logging service instance.</param>
    /// <param name="exception">The exception to log.</param>
    /// <param name="message">The log message.</param>
    /// <param name="correlationId">Optional correlation ID for tracing.</param>
    /// <param name="metadata">Optional metadata to include with the log entry.</param>
    /// <param name="callerMemberName">Automatically captured caller member name.</param>
    /// <param name="callerFilePath">Automatically captured caller file path.</param>
    public static void LogDebugWithSource(
        this IUnifiedLoggingService logger,
        Exception exception,
        string message,
        string? correlationId = null,
        object? metadata = null,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "")
    {
        string sourceContext = FormatSourceContext(callerFilePath, callerMemberName);
        string enrichedMessage = $"[{sourceContext}] {message}";
        logger.LogDebug(exception, enrichedMessage, correlationId, metadata);
    }

    /// <summary>
    /// Log an information message with automatic caller information
    /// </summary>
    /// <param name="logger">The unified logging service instance.</param>
    /// <param name="message">The log message.</param>
    /// <param name="correlationId">Optional correlation ID for tracing.</param>
    /// <param name="metadata">Optional metadata to include with the log entry.</param>
    /// <param name="callerMemberName">Automatically captured caller member name.</param>
    /// <param name="callerFilePath">Automatically captured caller file path.</param>
    public static void LogInformationWithSource(
        this IUnifiedLoggingService logger,
        string message,
        string? correlationId = null,
        object? metadata = null,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "")
    {
        string sourceContext = FormatSourceContext(callerFilePath, callerMemberName);
        string enrichedMessage = $"[{sourceContext}] {message}";
        logger.LogInformation(enrichedMessage, correlationId, metadata);
    }

    /// <summary>
    /// Log a warning message with automatic caller information
    /// </summary>
    /// <param name="logger">The unified logging service instance.</param>
    /// <param name="message">The log message.</param>
    /// <param name="correlationId">Optional correlation ID for tracing.</param>
    /// <param name="metadata">Optional metadata to include with the log entry.</param>
    /// <param name="callerMemberName">Automatically captured caller member name.</param>
    /// <param name="callerFilePath">Automatically captured caller file path.</param>
    public static void LogWarningWithSource(
        this IUnifiedLoggingService logger,
        string message,
        string? correlationId = null,
        object? metadata = null,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "")
    {
        string sourceContext = FormatSourceContext(callerFilePath, callerMemberName);
        string enrichedMessage = $"[{sourceContext}] {message}";
        logger.LogWarning(enrichedMessage, correlationId, metadata);
    }

    /// <summary>
    /// Log a warning message with exception and automatic caller information
    /// </summary>
    /// <param name="logger">The unified logging service instance.</param>
    /// <param name="exception">The exception to log.</param>
    /// <param name="message">The log message.</param>
    /// <param name="correlationId">Optional correlation ID for tracing.</param>
    /// <param name="metadata">Optional metadata to include with the log entry.</param>
    /// <param name="callerMemberName">Automatically captured caller member name.</param>
    /// <param name="callerFilePath">Automatically captured caller file path.</param>
    public static void LogWarningWithSource(
        this IUnifiedLoggingService logger,
        Exception exception,
        string message,
        string? correlationId = null,
        object? metadata = null,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "")
    {
        string sourceContext = FormatSourceContext(callerFilePath, callerMemberName);
        string enrichedMessage = $"[{sourceContext}] {message}";
        logger.LogWarning(exception, enrichedMessage, correlationId, metadata);
    }

    /// <summary>
    /// Log an error message with automatic caller information
    /// </summary>
    /// <param name="logger">The unified logging service instance.</param>
    /// <param name="message">The log message.</param>
    /// <param name="correlationId">Optional correlation ID for tracing.</param>
    /// <param name="metadata">Optional metadata to include with the log entry.</param>
    /// <param name="callerMemberName">Automatically captured caller member name.</param>
    /// <param name="callerFilePath">Automatically captured caller file path.</param>
    public static void LogErrorWithSource(
        this IUnifiedLoggingService logger,
        string message,
        string? correlationId = null,
        object? metadata = null,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "")
    {
        string sourceContext = FormatSourceContext(callerFilePath, callerMemberName);
        string enrichedMessage = $"[{sourceContext}] {message}";
        logger.LogError(enrichedMessage, correlationId, metadata);
    }

    /// <summary>
    /// Log an error message with exception and automatic caller information
    /// </summary>
    /// <param name="logger">The unified logging service instance.</param>
    /// <param name="exception">The exception to log.</param>
    /// <param name="message">The log message.</param>
    /// <param name="correlationId">Optional correlation ID for tracing.</param>
    /// <param name="metadata">Optional metadata to include with the log entry.</param>
    /// <param name="callerMemberName">Automatically captured caller member name.</param>
    /// <param name="callerFilePath">Automatically captured caller file path.</param>
    public static void LogErrorWithSource(
        this IUnifiedLoggingService logger,
        Exception exception,
        string message,
        string? correlationId = null,
        object? metadata = null,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "")
    {
        string sourceContext = FormatSourceContext(callerFilePath, callerMemberName);
        string enrichedMessage = $"[{sourceContext}] {message}";
        logger.LogError(exception, enrichedMessage, correlationId, metadata);
    }

    /// <summary>
    /// Log a critical message with automatic caller information
    /// </summary>
    /// <param name="logger">The unified logging service instance.</param>
    /// <param name="message">The log message.</param>
    /// <param name="correlationId">Optional correlation ID for tracing.</param>
    /// <param name="metadata">Optional metadata to include with the log entry.</param>
    /// <param name="callerMemberName">Automatically captured caller member name.</param>
    /// <param name="callerFilePath">Automatically captured caller file path.</param>
    public static void LogCriticalWithSource(
        this IUnifiedLoggingService logger,
        string message,
        string? correlationId = null,
        object? metadata = null,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "")
    {
        string sourceContext = FormatSourceContext(callerFilePath, callerMemberName);
        string enrichedMessage = $"[{sourceContext}] {message}";
        logger.LogCritical(enrichedMessage, correlationId, metadata);
    }

    /// <summary>
    /// Log a critical message with exception and automatic caller information
    /// </summary>
    /// <param name="logger">The unified logging service instance.</param>
    /// <param name="exception">The exception to log.</param>
    /// <param name="message">The log message.</param>
    /// <param name="correlationId">Optional correlation ID for tracing.</param>
    /// <param name="metadata">Optional metadata to include with the log entry.</param>
    /// <param name="callerMemberName">Automatically captured caller member name.</param>
    /// <param name="callerFilePath">Automatically captured caller file path.</param>
    public static void LogCriticalWithSource(
        this IUnifiedLoggingService logger,
        Exception exception,
        string message,
        string? correlationId = null,
        object? metadata = null,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "")
    {
        string sourceContext = FormatSourceContext(callerFilePath, callerMemberName);
        string enrichedMessage = $"[{sourceContext}] {message}";
        logger.LogCritical(exception, enrichedMessage, correlationId, metadata);
    }

    /// <summary>
    /// Format source context from file path and method name
    /// Example: "GcodeHarvestService.ImportSelectedFilesAsync"
    /// </summary>
    /// <param name="filePath">The full file path of the caller.</param>
    /// <param name="methodName">The method name of the caller.</param>
    private static string FormatSourceContext(string filePath, string methodName)
    {
        // Extract the class name from file path
        // e.g., "/home/pi/pfarm/src/infra/Services/Gcode/GcodeHarvestService.cs" -> "GcodeHarvestService"
        string fileName = Path.GetFileNameWithoutExtension(filePath);
        return $"{fileName}.{methodName}";
    }
}
