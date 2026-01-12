using System;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Telemetry;

public interface IUnifiedLoggingService
{
    void LogDebug(string message, string? correlationId = null, object? metadata = null);
    void LogDebug(Exception exception, string message, string? correlationId = null, object? metadata = null);
    void LogInformation(string message, string? correlationId = null, object? metadata = null);
    void LogWarning(string message, string? correlationId = null, object? metadata = null);
    void LogWarning(Exception exception, string message, string? correlationId = null, object? metadata = null);
    void LogError(string message, string? correlationId = null, object? metadata = null);
    void LogError(Exception exception, string message, string? correlationId = null, object? metadata = null);
    void LogCritical(string message, string? correlationId = null, object? metadata = null);
    void LogCritical(Exception exception, string message, string? correlationId = null, object? metadata = null);

    // Context-aware logging
    void LogWithContext(LogLevel level, string category, string message, string? correlationId = null, object? metadata = null, object? context = null, Exception? exception = null);
}

public sealed class UnifiedLoggingService : IUnifiedLoggingService, IDisposable
{
    private readonly ILogger<UnifiedLoggingService> _logger;
    private readonly IPrintFarmerTelemetryService _telemetry;
    private readonly ActivitySource _activitySource = new ActivitySource("PrintFarmer.Logging");

    public UnifiedLoggingService(ILogger<UnifiedLoggingService> logger, IPrintFarmerTelemetryService telemetry)
    {
        _logger = logger;
        _telemetry = telemetry;
    }

    public void LogDebug(string message, string? correlationId = null, object? metadata = null)
    {
        LogWithTelemetry(LogLevel.Debug, "Debug", message, correlationId, metadata);
    }

    public void LogDebug(Exception exception, string message, string? correlationId = null, object? metadata = null)
    {
        LogWithTelemetry(LogLevel.Debug, "Debug", message, correlationId, metadata, exception);
    }

    public void LogInformation(string message, string? correlationId = null, object? metadata = null)
    {
        LogWithTelemetry(LogLevel.Information, "Information", message, correlationId, metadata);
    }

    public void LogWarning(string message, string? correlationId = null, object? metadata = null)
    {
        LogWithTelemetry(LogLevel.Warning, "Warning", message, correlationId, metadata);
    }

    public void LogWarning(Exception exception, string message, string? correlationId = null, object? metadata = null)
    {
        LogWithTelemetry(LogLevel.Warning, "Warning", message, correlationId, metadata, exception);
    }

    public void LogError(string message, string? correlationId = null, object? metadata = null)
    {
        LogWithTelemetry(LogLevel.Error, "Error", message, correlationId, metadata);
    }

    public void LogError(Exception exception, string message, string? correlationId = null, object? metadata = null)
    {
        LogWithTelemetry(LogLevel.Error, "Error", message, correlationId, metadata, exception);
    }

    public void LogCritical(string message, string? correlationId = null, object? metadata = null)
    {
        LogWithTelemetry(LogLevel.Critical, "Critical", message, correlationId, metadata);
    }

    public void LogCritical(Exception exception, string message, string? correlationId = null, object? metadata = null)
    {
        LogWithTelemetry(LogLevel.Critical, "Critical", message, correlationId, metadata, exception);
    }

    public void LogWithContext(LogLevel level, string category, string message, string? correlationId = null, object? metadata = null, object? context = null, Exception? exception = null)
    {
        using Activity? activity = _activitySource.StartActivity($"Log.{category}");

        // Add context to telemetry
        if (activity != null)
        {
            _ = activity.SetTag("log.level", level.ToString());
            _ = activity.SetTag("log.category", category);
            _ = activity.SetTag("log.message", message);
            if (!string.IsNullOrEmpty(correlationId))
            {
                _ = activity.SetTag("log.correlationId", correlationId);
            }
            if (metadata != null)
            {
                _ = activity.SetTag("log.metadata", JsonSerializer.Serialize(metadata));
            }
            if (context != null)
            {
                _ = activity.SetTag("log.context", JsonSerializer.Serialize(context));
            }
            if (exception != null)
            {
                _ = activity.SetTag("error", true);
                _ = activity.SetTag("exception.type", exception.GetType().Name);
                _ = activity.SetTag("exception.message", exception.Message);
                _ = activity.SetTag("exception.stackTrace", exception.StackTrace);
                _ = activity.AddException(exception);
                _ = activity.SetStatus(ActivityStatusCode.Error, exception.Message);
            }
        }

        // Log to structured logger
        if (exception != null)
        {
            _logger.Log(level, exception, "[{Category}] {Message} CorrelationId: {CorrelationId} Metadata: {Metadata} Context: {Context}",
                category, message, correlationId ?? "None", metadata != null ? JsonSerializer.Serialize(metadata) : "None", context ?? "None");
        }
        else
        {
            _logger.Log(level, "[{Category}] {Message} CorrelationId: {CorrelationId} Metadata: {Metadata} Context: {Context}",
                category, message, correlationId ?? "None", metadata != null ? JsonSerializer.Serialize(metadata) : "None", context ?? "None");
        }
    }

    private void LogWithTelemetry(LogLevel level, string category, string message, string? correlationId, object? metadata, Exception? exception = null)
    {
        using Activity? activity = _activitySource.StartActivity($"Log.{category}");

        if (activity != null)
        {
            _ = activity.SetTag("log.level", level.ToString());
            _ = activity.SetTag("log.category", category);
            _ = activity.SetTag("log.message", message);
            if (!string.IsNullOrEmpty(correlationId))
            {
                _ = activity.SetTag("log.correlationId", correlationId);
            }
            if (metadata != null)
            {
                _ = activity.SetTag("log.metadata", JsonSerializer.Serialize(metadata));
            }
        }

        // Log to structured logger with telemetry context (file/console)
        object?[] loggerArgs = new object?[]
        {
            category,
            message,
            correlationId ?? "None",
            metadata != null ? JsonSerializer.Serialize(metadata) : "None"
        };
        if (exception != null)
        {
            _logger.Log(level, exception,
                "[{Category}] {Message} CorrelationId: {CorrelationId} Metadata: {Metadata}",
                loggerArgs);
        }
        else
        {
            _logger.Log(level,
                "[{Category}] {Message} CorrelationId: {CorrelationId} Metadata: {Metadata}",
                loggerArgs);
        }
    }


    public void Dispose()
    {
        _activitySource?.Dispose();
    }
}


