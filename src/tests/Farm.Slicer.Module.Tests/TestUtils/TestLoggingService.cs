using System;
using Farm.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Tests.TestUtils
{
    public class TestLoggingService : IUnifiedLoggingService
    {
        public void LogDebug(string message, string? correlationId = null, object? metadata = null)
        {
        }

        public void LogDebug(Exception exception, string message, string? correlationId = null, object? metadata = null)
        {
        }

        public void LogInformation(string message, string? correlationId = null, object? metadata = null)
        {
        }

        public void LogWarning(string message, string? correlationId = null, object? metadata = null)
        {
        }

        public void LogWarning(Exception exception, string message, string? correlationId = null, object? metadata = null)
        {
        }

        public void LogError(string message, string? correlationId = null, object? metadata = null)
        {
        }

        public void LogError(Exception exception, string message, string? correlationId = null, object? metadata = null)
        {
        }

        public void LogCritical(string message, string? correlationId = null, object? metadata = null)
        {
        }

        public void LogCritical(Exception exception, string message, string? correlationId = null, object? metadata = null)
        {
        }

        public void LogWithContext(LogLevel level, string category, string message, string? correlationId = null, object? metadata = null, object? context = null, Exception? exception = null)
        {
        }
    }
}
