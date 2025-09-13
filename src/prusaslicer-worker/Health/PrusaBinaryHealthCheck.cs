using Farm.PrusaSlicer.Worker.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Farm.PrusaSlicer.Worker.Health;

public sealed class PrusaBinaryHealthCheck : IHealthCheck
{
    private readonly IPrusaBinaryDetector _detector;
    public PrusaBinaryHealthCheck(IPrusaBinaryDetector detector) => _detector = detector;
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(_detector.IsRealBinaryPresent()
            ? HealthCheckResult.Healthy("Real PrusaSlicer binary present")
            : HealthCheckResult.Unhealthy("PrusaSlicer binary missing or stub only"));
}
