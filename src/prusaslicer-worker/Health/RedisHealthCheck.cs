using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Farm.PrusaSlicer.Worker.Health;

internal sealed class RedisHealthCheck : IHealthCheck
{
    private readonly IConnectionMultiplexer _redis;
    public RedisHealthCheck(IConnectionMultiplexer redis) => _redis = redis;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return Task.FromResult(_redis.IsConnected
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Redis not connected"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Redis connection failed", ex));
        }
    }
}
