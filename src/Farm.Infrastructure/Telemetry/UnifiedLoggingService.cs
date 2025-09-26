using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Farm.Infrastructure.Data;

namespace Farm.Infrastructure.Telemetry;

public interface IUnifiedLoggingService
{
    void LogDebug(string message, string? correlationId = null, object? metadata = null, params object[] args);
    void LogDebug(Exception exception, string message, string? correlationId = null, object? metadata = null, params object[] args);
    void LogInformation(string message, string? correlationId = null, object? metadata = null, params object[] args);
    void LogWarning(string message, string? correlationId = null, object? metadata = null, params object[] args);
    void LogWarning(Exception exception, string message, string? correlationId = null, object? metadata = null, params object[] args);
    void LogError(string message, string? correlationId = null, object? metadata = null, params object[] args);
    void LogError(Exception exception, string message, string? correlationId = null, object? metadata = null, params object[] args);
    void LogCritical(string message, string? correlationId = null, object? metadata = null, params object[] args);
    void LogCritical(Exception exception, string message, string? correlationId = null, object? metadata = null, params object[] args);

    // Context-aware logging
    void LogWithContext(LogLevel level, string category, string message, string? correlationId = null, object? metadata = null, object? context = null, Exception? exception = null);
}

public sealed class UnifiedLoggingService : IUnifiedLoggingService, IDisposable
{
    private readonly ILogger<UnifiedLoggingService> _logger;
    private readonly IPrintFarmerTelemetryService _telemetryService;
    private readonly AppDbContext _dbContext;
    private readonly ActivitySource _activitySource = new ActivitySource("PrintFarmer.Logging");

    public UnifiedLoggingService(ILogger<UnifiedLoggingService> logger, IPrintFarmerTelemetryService telemetryService, AppDbContext dbContext)
    {
        _logger = logger;
        _telemetryService = telemetryService;
        _dbContext = dbContext;
    }

    public void LogDebug(string message, string? correlationId = null, object? metadata = null, params object[] args)
    {
        LogWithTelemetry(LogLevel.Debug, "Debug", message, correlationId, metadata, args);
    }

    public void LogDebug(Exception exception, string message, string? correlationId = null, object? metadata = null, params object[] args)
    {
        LogWithTelemetry(LogLevel.Debug, "Debug", message, correlationId, metadata, args, exception);
    }

    public void LogInformation(string message, string? correlationId = null, object? metadata = null, params object[] args)
    {
        LogWithTelemetry(LogLevel.Information, "Information", message, correlationId, metadata, args);
    }

    public void LogWarning(string message, string? correlationId = null, object? metadata = null, params object[] args)
    {
        LogWithTelemetry(LogLevel.Warning, "Warning", message, correlationId, metadata, args);
    }

    public void LogWarning(Exception exception, string message, string? correlationId = null, object? metadata = null, params object[] args)
    {
        LogWithTelemetry(LogLevel.Warning, "Warning", message, correlationId, metadata, args, exception);
    }

    public void LogError(string message, string? correlationId = null, object? metadata = null, params object[] args)
    {
        LogWithTelemetry(LogLevel.Error, "Error", message, correlationId, metadata, args);
    }

    public void LogError(Exception exception, string message, string? correlationId = null, object? metadata = null, params object[] args)
    {
        LogWithTelemetry(LogLevel.Error, "Error", message, correlationId, metadata, args, exception);
    }

    public void LogCritical(string message, string? correlationId = null, object? metadata = null, params object[] args)
    {
        LogWithTelemetry(LogLevel.Critical, "Critical", message, correlationId, metadata, args);
    }

    public void LogCritical(Exception exception, string message, string? correlationId = null, object? metadata = null, params object[] args)
    {
        LogWithTelemetry(LogLevel.Critical, "Critical", message, correlationId, metadata, args, exception);
    }

    public void LogWithContext(LogLevel level, string category, string message, string? correlationId = null, object? metadata = null, object? context = null, Exception? exception = null)
    {
        using Activity? activity = _activitySource.StartActivity($"Log.{category}");

        // Add context to telemetry
        if (activity != null)
        {
            activity.SetTag("log.level", level.ToString());
            activity.SetTag("log.category", category);
            activity.SetTag("log.message", message);
            if (!string.IsNullOrEmpty(correlationId))
            {
                activity.SetTag("log.correlationId", correlationId);
            }
            if (metadata != null)
            {
                activity.SetTag("log.metadata", System.Text.Json.JsonSerializer.Serialize(metadata));
            }
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
                activity.AddException(exception);
                activity.SetStatus(ActivityStatusCode.Error, exception.Message);
            }
        }

        // Log to structured logger
        if (exception != null)
        {
            _logger.Log(level, exception, "[{Category}] {Message} CorrelationId: {CorrelationId} Metadata: {Metadata} Context: {Context}",
                category, message, correlationId ?? "None", metadata != null ? System.Text.Json.JsonSerializer.Serialize(metadata) : "None", context ?? "None");
        }
        else
        {
            _logger.Log(level, "[{Category}] {Message} CorrelationId: {CorrelationId} Metadata: {Metadata} Context: {Context}",
                category, message, correlationId ?? "None", metadata != null ? System.Text.Json.JsonSerializer.Serialize(metadata) : "None", context ?? "None");
        }
    }

    private void LogWithTelemetry(LogLevel level, string category, string message, string? correlationId, object? metadata, object[] args, Exception? exception = null)
    {
        using Activity? activity = _activitySource.StartActivity($"Log.{category}");

        string formattedMessage = args.Length > 0 ? string.Format(message, args) : message;

        if (activity != null)
        {
            activity.SetTag("log.level", level.ToString());
            activity.SetTag("log.category", category);
            activity.SetTag("log.message", formattedMessage);
            if (!string.IsNullOrEmpty(correlationId))
            {
                activity.SetTag("log.correlationId", correlationId);
            }
            if (metadata != null)
            {
                activity.SetTag("log.metadata", System.Text.Json.JsonSerializer.Serialize(metadata));
            }
        }

        // Persist to SystemLog table
        var systemLog = new Farm.Infrastructure.Domain.SystemLog
        {
            Timestamp = DateTime.UtcNow,
            Level = level.ToString(),
            Message = formattedMessage,
            Exception = exception?.ToString(),
            Source = category,
            CorrelationId = correlationId,
            Metadata = metadata != null ? System.Text.Json.JsonSerializer.Serialize(metadata) : null
        };
        try
        {
            _dbContext.SystemLogs.Add(systemLog);
            _dbContext.SaveChanges();
        }
        catch (Exception dbEx)
        {
            // Fallback: log DB error to standard logger
            _logger.LogError(dbEx, "Failed to persist log entry to SystemLog table");
        }

        // Log to structured logger with telemetry context
        if (exception != null)
        {
            _logger.Log(level, exception, "[{Category}] {Message} CorrelationId: {CorrelationId} Metadata: {Metadata}", category, formattedMessage, correlationId ?? "None", metadata != null ? System.Text.Json.JsonSerializer.Serialize(metadata) : "None");
        }
        else
        {
            _logger.Log(level, "[{Category}] {Message} CorrelationId: {CorrelationId} Metadata: {Metadata}", category, formattedMessage, correlationId ?? "None", metadata != null ? System.Text.Json.JsonSerializer.Serialize(metadata) : "None");
        }
    }


    public void Dispose()
    {
        _activitySource?.Dispose();
    }
}


