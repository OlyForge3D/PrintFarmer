using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Farm.OrcaSlicer.Worker.Health;

public class WorkerLivenessHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(HealthCheckResult.Healthy("OrcaSlicer worker alive"));
    }
}
