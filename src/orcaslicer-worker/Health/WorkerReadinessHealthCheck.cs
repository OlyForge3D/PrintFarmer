using Farm.Infrastructure.Telemetry;
using Farm.Slicer.Worker.Core;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Farm.OrcaSlicer.Worker.Health;

public class WorkerReadinessHealthCheck(IWorkerStateService workerStateService, ILogger<WorkerReadinessHealthCheck> logger) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            WorkerState state = workerStateService.GetWorkerState();
            bool isReady = state.IsInitialized && !state.IsShuttingDown && state.ActiveJobs < state.MaxConcurrentJobs;
            logger.LogDebug("Readiness - Ready {IsReady} Active {StateActiveJobs}/{StateMaxConcurrentJobs}", isReady, state.ActiveJobs, state.MaxConcurrentJobs);
            Dictionary<string, object> data = new Dictionary<string, object>
            {
                ["initialized"] = state.IsInitialized,
                ["shuttingDown"] = state.IsShuttingDown,
                ["activeJobs"] = state.ActiveJobs,
                ["maxConcurrentJobs"] = state.MaxConcurrentJobs,
                ["workerId"] = state.WorkerId
            };
            return Task.FromResult(isReady ? HealthCheckResult.Healthy("Worker ready", data) : HealthCheckResult.Unhealthy("Worker not ready", null, data));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Readiness check failed");
            return Task.FromResult(HealthCheckResult.Unhealthy("Readiness check failed", ex));
        }
    }
}
