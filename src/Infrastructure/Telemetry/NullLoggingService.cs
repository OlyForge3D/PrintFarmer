using Microsoft.Extensions.Logging;
using System;

namespace Farm.Infrastructure.Telemetry;

/// <summary>
/// A no-op implementation of IUnifiedLoggingService for use in tests or as a fallback.
/// </summary>
public sealed class NullLoggingService : IUnifiedLoggingService
{
    public void LogDebug(string message, string? correlationId = null, object? metadata = null, params object[] args) { }
    public void LogDebug(Exception exception, string message, string? correlationId = null, object? metadata = null, params object[] args) { }
    public void LogInformation(string message, string? correlationId = null, object? metadata = null, params object[] args) { }
    public void LogWarning(string message, string? correlationId = null, object? metadata = null, params object[] args) { }
    public void LogWarning(Exception exception, string message, string? correlationId = null, object? metadata = null, params object[] args) { }
    public void LogError(string message, string? correlationId = null, object? metadata = null, params object[] args) { }
    public void LogError(Exception exception, string message, string? correlationId = null, object? metadata = null, params object[] args) { }
    public void LogCritical(string message, string? correlationId = null, object? metadata = null, params object[] args) { }
    public void LogCritical(Exception exception, string message, string? correlationId = null, object? metadata = null, params object[] args) { }
    public void LogWithContext(LogLevel level, string category, string message, string? correlationId = null, object? metadata = null, object? context = null, Exception? exception = null) { }
}
