using Farm.OrcaSlicer.Worker.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Farm.OrcaSlicer.Worker.Health;

public sealed class OrcaBinaryHealthCheck(IOrcaBinaryDetector detector) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(detector.IsRealBinaryPresent() ? HealthCheckResult.Healthy("Real OrcaSlicer binary present") : HealthCheckResult.Unhealthy("OrcaSlicer binary missing or stub only"));
}
