using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Farm.PrusaSlicer.Worker.Health;

/// <summary>
/// Liveness health check - indicates if the worker process is alive and responding
/// This is used by Kubernetes liveness probes to restart unhealthy containers
/// </summary>
public class WorkerLivenessHealthCheck(ILogger<WorkerLivenessHealthCheck> logger) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // Basic liveness check - if we can execute this code, the process is alive
            var uptime = DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
            
            logger.LogDebug("Liveness check - Worker has been running for {Uptime}", uptime);
            
            return Task.FromResult(HealthCheckResult.Healthy("Worker process is alive", new Dictionary<string, object>
            {
                ["uptime"] = uptime.ToString(),
                ["processId"] = Environment.ProcessId,
                ["machineName"] = Environment.MachineName
            }));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Liveness check failed");
            return Task.FromResult(HealthCheckResult.Unhealthy("Liveness check failed", ex));
        }
    }
}