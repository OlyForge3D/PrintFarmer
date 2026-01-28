using System;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Telemetry;

/// <summary>
/// Service for unified structured logging with telemetry correlation support.
/// Provides consistent logging across the application with metadata and correlation ID tracking.
/// </summary>
public interface IUnifiedLoggingService
{
    /// <summary>
    /// Logs a debug-level message.
    /// </summary>
    /// <param name="message">The message to log.</param>
    /// <param name="correlationId">Optional correlation ID for request tracing.</param>
    /// <param name="metadata">Optional structured metadata to include.</param>
    void LogDebug(string message, string? correlationId = null, object? metadata = null);

    /// <summary>
    /// Logs a debug-level message with an exception.
    /// </summary>
    /// <param name="exception">The exception to log.</param>
    /// <param name="message">The message to log.</param>
    /// <param name="correlationId">Optional correlation ID for request tracing.</param>
    /// <param name="metadata">Optional structured metadata to include.</param>
    void LogDebug(Exception exception, string message, string? correlationId = null, object? metadata = null);

    /// <summary>
    /// Logs an information-level message.
    /// </summary>
    /// <param name="message">The message to log.</param>
    /// <param name="correlationId">Optional correlation ID for request tracing.</param>
    /// <param name="metadata">Optional structured metadata to include.</param>
    void LogInformation(string message, string? correlationId = null, object? metadata = null);

    /// <summary>
    /// Logs a warning-level message.
    /// </summary>
    /// <param name="message">The message to log.</param>
    /// <param name="correlationId">Optional correlation ID for request tracing.</param>
    /// <param name="metadata">Optional structured metadata to include.</param>
    void LogWarning(string message, string? correlationId = null, object? metadata = null);

    /// <summary>
    /// Logs a warning-level message with an exception.
    /// </summary>
    /// <param name="exception">The exception to log.</param>
    /// <param name="message">The message to log.</param>
    /// <param name="correlationId">Optional correlation ID for request tracing.</param>
    /// <param name="metadata">Optional structured metadata to include.</param>
    void LogWarning(Exception exception, string message, string? correlationId = null, object? metadata = null);

    /// <summary>
    /// Logs an error-level message.
    /// </summary>
    /// <param name="message">The message to log.</param>
    /// <param name="correlationId">Optional correlation ID for request tracing.</param>
    /// <param name="metadata">Optional structured metadata to include.</param>
    void LogError(string message, string? correlationId = null, object? metadata = null);

    /// <summary>
    /// Logs an error-level message with an exception.
    /// </summary>
    /// <param name="exception">The exception to log.</param>
    /// <param name="message">The message to log.</param>
    /// <param name="correlationId">Optional correlation ID for request tracing.</param>
    /// <param name="metadata">Optional structured metadata to include.</param>
    void LogError(Exception exception, string message, string? correlationId = null, object? metadata = null);

    /// <summary>
    /// Logs a critical-level message.
    /// </summary>
    /// <param name="message">The message to log.</param>
    /// <param name="correlationId">Optional correlation ID for request tracing.</param>
    /// <param name="metadata">Optional structured metadata to include.</param>
    void LogCritical(string message, string? correlationId = null, object? metadata = null);

    /// <summary>
    /// Logs a critical-level message with an exception.
    /// </summary>
    /// <param name="exception">The exception to log.</param>
    /// <param name="message">The message to log.</param>
    /// <param name="correlationId">Optional correlation ID for request tracing.</param>
    /// <param name="metadata">Optional structured metadata to include.</param>
    void LogCritical(Exception exception, string message, string? correlationId = null, object? metadata = null);

    /// <summary>
    /// Logs a message with full context including category and additional context object.
    /// </summary>
    /// <param name="level">The log level.</param>
    /// <param name="category">The log category for grouping.</param>
    /// <param name="message">The message to log.</param>
    /// <param name="correlationId">Optional correlation ID for request tracing.</param>
    /// <param name="metadata">Optional structured metadata to include.</param>
    /// <param name="context">Optional additional context object.</param>
    /// <param name="exception">Optional exception to include.</param>
    void LogWithContext(LogLevel level, string category, string message, string? correlationId = null, object? metadata = null, object? context = null, Exception? exception = null);
}

public sealed class UnifiedLoggingService(ILogger<UnifiedLoggingService> logger) : IUnifiedLoggingService, IDisposable
{
    private readonly ILogger<UnifiedLoggingService> _logger = logger;
    private readonly ActivitySource _activitySource = new ActivitySource("PrintFarmer.Logging");

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
            _logger.Log(
                level,
                "[{Category}] {Message} CorrelationId: {CorrelationId} Metadata: {Metadata}",
                loggerArgs);
        }
    }

    public void Dispose()
    {
        _activitySource?.Dispose();
    }
}
