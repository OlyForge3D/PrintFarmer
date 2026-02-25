using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Services.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Slicer.Module.Tests.RateLimiting;

public class SliceJobRateLimitTests
{

    [Fact]
    public async Task EnforcesHourlyLimit()
    {
        RateLimitOptions opts = new RateLimitOptions
        {
            SliceJobs = new SliceJobRateLimitOptions { MaxAttemptsPerHour = 3, MaxAttemptsPerDay = 10 }
        };
        InMemoryRateLimitService svc = new InMemoryRateLimitService(opts, NullLogger<InMemoryRateLimitService>.Instance);
        Guid userId = Guid.NewGuid();
        for (int i = 0; i < 3; i++)
        {
            RateLimitResult check = await svc.CheckSliceJobSubmitLimitAsync(userId);
            Assert.True(check.IsAllowed);
            await svc.RecordSliceJobSubmitAttemptAsync(userId);
        }
        RateLimitResult finalCheck = await svc.CheckSliceJobSubmitLimitAsync(userId);
        Assert.False(finalCheck.IsAllowed);
        _ = Assert.NotNull(finalCheck.RetryAfter);
    }

    [Fact]
    public async Task EnforcesDailyLimit()
    {
        RateLimitOptions opts = new RateLimitOptions
        {
            SliceJobs = new SliceJobRateLimitOptions { MaxAttemptsPerHour = 100, MaxAttemptsPerDay = 5 }
        };
        InMemoryRateLimitService svc = new InMemoryRateLimitService(opts, NullLogger<InMemoryRateLimitService>.Instance);
        Guid userId = Guid.NewGuid();
        for (int i = 0; i < 5; i++)
        {
            RateLimitResult check = await svc.CheckSliceJobSubmitLimitAsync(userId);
            Assert.True(check.IsAllowed);
            await svc.RecordSliceJobSubmitAttemptAsync(userId);
        }
        RateLimitResult finalCheck = await svc.CheckSliceJobSubmitLimitAsync(userId);
        Assert.False(finalCheck.IsAllowed);
    }
}
