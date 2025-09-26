using System;
using Farm.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Tests.TestUtils
{
    public class TestLoggingService : IUnifiedLoggingService
    {
        public void LogDebug(string message, params object[] args) { }
        public void LogDebug(Exception exception, string message, params object[] args) { }
        public void LogInformation(string message, params object[] args) { }
        public void LogWarning(string message, params object[] args) { }
        public void LogWarning(Exception exception, string message, params object[] args) { }
        public void LogError(string message, params object[] args) { }
        public void LogError(Exception exception, string message, params object[] args) { }
        public void LogCritical(string message, params object[] args) { }
        public void LogCritical(Exception exception, string message, params object[] args) { }
        public void LogWithContext(LogLevel level, string category, string message, object? context = null, Exception? exception = null) { }
    }
}
