using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.RateLimiting;
using Xunit;

namespace Farm.Web.Api.Tests.RateLimiting;

public class SliceJobRateLimitTests
{
    private class StubLogger : IUnifiedLoggingService
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
        public void LogWithContext(Microsoft.Extensions.Logging.LogLevel level, string category, string message, string? correlationId = null, object? metadata = null, object? context = null, Exception? exception = null) { }
    }

    [Fact]
    public async Task EnforcesHourlyLimit()
    {
        var opts = new RateLimitOptions
        {
            SliceJobs = new SliceJobRateLimitOptions { MaxAttemptsPerHour = 3, MaxAttemptsPerDay = 10 }
        };
        var svc = new InMemoryRateLimitService(opts, new StubLogger());
        Guid userId = Guid.NewGuid();
        for (int i = 0; i < 3; i++)
        {
            var check = await svc.CheckSliceJobSubmitLimitAsync(userId);
            Assert.True(check.IsAllowed);
            await svc.RecordSliceJobSubmitAttemptAsync(userId);
        }
        var finalCheck = await svc.CheckSliceJobSubmitLimitAsync(userId);
        Assert.False(finalCheck.IsAllowed);
        Assert.NotNull(finalCheck.RetryAfter);
    }

    [Fact]
    public async Task EnforcesDailyLimit()
    {
        var opts = new RateLimitOptions
        {
            SliceJobs = new SliceJobRateLimitOptions { MaxAttemptsPerHour = 100, MaxAttemptsPerDay = 5 }
        };
        var svc = new InMemoryRateLimitService(opts, new StubLogger());
        Guid userId = Guid.NewGuid();
        for (int i = 0; i < 5; i++)
        {
            var check = await svc.CheckSliceJobSubmitLimitAsync(userId);
            Assert.True(check.IsAllowed);
            await svc.RecordSliceJobSubmitAttemptAsync(userId);
        }
        var finalCheck = await svc.CheckSliceJobSubmitLimitAsync(userId);
        Assert.False(finalCheck.IsAllowed);
    }
}
