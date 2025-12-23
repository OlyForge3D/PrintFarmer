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
    public static void LogDebugWithSource(
        this IUnifiedLoggingService logger,
        string message,
        string? correlationId = null,
        object? metadata = null,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "")
    {
        var sourceContext = FormatSourceContext(callerFilePath, callerMemberName);
        var enrichedMessage = $"[{sourceContext}] {message}";
        logger.LogDebug(enrichedMessage, correlationId, metadata);
    }

    /// <summary>
    /// Log a debug message with exception and automatic caller information
    /// </summary>
    public static void LogDebugWithSource(
        this IUnifiedLoggingService logger,
        Exception exception,
        string message,
        string? correlationId = null,
        object? metadata = null,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "")
    {
        var sourceContext = FormatSourceContext(callerFilePath, callerMemberName);
        var enrichedMessage = $"[{sourceContext}] {message}";
        logger.LogDebug(exception, enrichedMessage, correlationId, metadata);
    }

    /// <summary>
    /// Log an information message with automatic caller information
    /// </summary>
    public static void LogInformationWithSource(
        this IUnifiedLoggingService logger,
        string message,
        string? correlationId = null,
        object? metadata = null,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "")
    {
        var sourceContext = FormatSourceContext(callerFilePath, callerMemberName);
        var enrichedMessage = $"[{sourceContext}] {message}";
        logger.LogInformation(enrichedMessage, correlationId, metadata);
    }

    /// <summary>
    /// Log a warning message with automatic caller information
    /// </summary>
    public static void LogWarningWithSource(
        this IUnifiedLoggingService logger,
        string message,
        string? correlationId = null,
        object? metadata = null,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "")
    {
        var sourceContext = FormatSourceContext(callerFilePath, callerMemberName);
        var enrichedMessage = $"[{sourceContext}] {message}";
        logger.LogWarning(enrichedMessage, correlationId, metadata);
    }

    /// <summary>
    /// Log a warning message with exception and automatic caller information
    /// </summary>
    public static void LogWarningWithSource(
        this IUnifiedLoggingService logger,
        Exception exception,
        string message,
        string? correlationId = null,
        object? metadata = null,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "")
    {
        var sourceContext = FormatSourceContext(callerFilePath, callerMemberName);
        var enrichedMessage = $"[{sourceContext}] {message}";
        logger.LogWarning(exception, enrichedMessage, correlationId, metadata);
    }

    /// <summary>
    /// Log an error message with automatic caller information
    /// </summary>
    public static void LogErrorWithSource(
        this IUnifiedLoggingService logger,
        string message,
        string? correlationId = null,
        object? metadata = null,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "")
    {
        var sourceContext = FormatSourceContext(callerFilePath, callerMemberName);
        var enrichedMessage = $"[{sourceContext}] {message}";
        logger.LogError(enrichedMessage, correlationId, metadata);
    }

    /// <summary>
    /// Log an error message with exception and automatic caller information
    /// </summary>
    public static void LogErrorWithSource(
        this IUnifiedLoggingService logger,
        Exception exception,
        string message,
        string? correlationId = null,
        object? metadata = null,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "")
    {
        var sourceContext = FormatSourceContext(callerFilePath, callerMemberName);
        var enrichedMessage = $"[{sourceContext}] {message}";
        logger.LogError(exception, enrichedMessage, correlationId, metadata);
    }

    /// <summary>
    /// Log a critical message with automatic caller information
    /// </summary>
    public static void LogCriticalWithSource(
        this IUnifiedLoggingService logger,
        string message,
        string? correlationId = null,
        object? metadata = null,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "")
    {
        var sourceContext = FormatSourceContext(callerFilePath, callerMemberName);
        var enrichedMessage = $"[{sourceContext}] {message}";
        logger.LogCritical(enrichedMessage, correlationId, metadata);
    }

    /// <summary>
    /// Log a critical message with exception and automatic caller information
    /// </summary>
    public static void LogCriticalWithSource(
        this IUnifiedLoggingService logger,
        Exception exception,
        string message,
        string? correlationId = null,
        object? metadata = null,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "")
    {
        var sourceContext = FormatSourceContext(callerFilePath, callerMemberName);
        var enrichedMessage = $"[{sourceContext}] {message}";
        logger.LogCritical(exception, enrichedMessage, correlationId, metadata);
    }

    /// <summary>
    /// Format source context from file path and method name
    /// Example: "GcodeHarvestService.ImportSelectedFilesAsync"
    /// </summary>
    private static string FormatSourceContext(string filePath, string methodName)
    {
        // Extract the class name from file path
        // e.g., "/home/pi/pfarm/src/infra/Services/Gcode/GcodeHarvestService.cs" -> "GcodeHarvestService"
        string fileName = Path.GetFileNameWithoutExtension(filePath);
        return $"{fileName}.{methodName}";
    }
}
