using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Farm.OrcaSlicer.Worker.Health;

internal sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return Task.FromResult(redis.IsConnected ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Redis not connected"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Redis connection failed", ex));
        }
    }
}
