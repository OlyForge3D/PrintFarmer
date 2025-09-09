using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Farm.PrusaSlicer.Worker.Health;

/// <summary>
/// Readiness health check - indicates if the worker is ready to accept and process jobs
/// This is used by Kubernetes readiness probes to determine if traffic should be sent to the pod
/// </summary>
public class WorkerReadinessHealthCheck(
    IWorkerStateService workerStateService,
    ILogger<WorkerReadinessHealthCheck> logger) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var state = workerStateService.GetWorkerState();

            // Check if worker is ready to accept jobs
            var isReady = state.IsInitialized &&
                         !state.IsShuttingDown &&
                         state.ActiveJobs < state.MaxConcurrentJobs;

            logger.LogDebug("Readiness check - Worker ready: {IsReady}, ActiveJobs: {ActiveJobs}/{MaxJobs}",
                isReady, state.ActiveJobs, state.MaxConcurrentJobs);

            var data = new Dictionary<string, object>
            {
                ["initialized"] = state.IsInitialized,
                ["shuttingDown"] = state.IsShuttingDown,
                ["activeJobs"] = state.ActiveJobs,
                ["maxConcurrentJobs"] = state.MaxConcurrentJobs,
                ["workerId"] = state.WorkerId
            };

            return Task.FromResult(isReady
                ? HealthCheckResult.Healthy("Worker is ready to accept jobs", data)
                : HealthCheckResult.Unhealthy("Worker is not ready to accept jobs", null, data));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Readiness check failed");
            return Task.FromResult(HealthCheckResult.Unhealthy("Readiness check failed", ex));
        }
    }
}