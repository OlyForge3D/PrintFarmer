using Farm.Infrastructure.Telemetry;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Farm.OrcaSlicer.Worker.Health;

public class WorkerLivenessHealthCheck(IUnifiedLoggingService logger) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            TimeSpan uptime = DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
            logger.LogDebug($"Liveness check - Worker up {uptime}");
            return Task.FromResult(HealthCheckResult.Healthy("Worker process alive", new Dictionary<string, object>
            {
                ["uptime"] = uptime.ToString(),
                ["processId"] = Environment.ProcessId,
                ["machineName"] = Environment.MachineName
            }));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Liveness check failed");
            return Task.FromResult(HealthCheckResult.Unhealthy("Liveness check failed", ex));
        }
    }
}
