using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Farm.OrcaSlicer.Worker.Health;

// Redis health check removed. Keep a lightweight stub to avoid breaking builds
// in environments where health check registration may still reference this type.
internal sealed class RedisHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(HealthCheckResult.Healthy("redis-check-removed"));
}
