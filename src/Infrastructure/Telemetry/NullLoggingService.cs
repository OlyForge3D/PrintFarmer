using System;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Telemetry;

/// <summary>
/// A no-op implementation of IUnifiedLoggingService for use in tests or as a fallback.
/// </summary>
public sealed class NullLoggingService : IUnifiedLoggingService
{
    public void LogDebug(string message, string? correlationId = null, object? metadata = null) { }
    public void LogDebug(Exception exception, string message, string? correlationId = null, object? metadata = null) { }
    public void LogInformation(string message, string? correlationId = null, object? metadata = null) { }
    public void LogWarning(string message, string? correlationId = null, object? metadata = null) { }
    public void LogWarning(Exception exception, string message, string? correlationId = null, object? metadata = null) { }
    public void LogError(string message, string? correlationId = null, object? metadata = null) { }
    public void LogError(Exception exception, string message, string? correlationId = null, object? metadata = null) { }
    public void LogCritical(string message, string? correlationId = null, object? metadata = null) { }
    public void LogCritical(Exception exception, string message, string? correlationId = null, object? metadata = null) { }
    public void LogWithContext(LogLevel level, string category, string message, string? correlationId = null, object? metadata = null, object? context = null, Exception? exception = null) { }
}
