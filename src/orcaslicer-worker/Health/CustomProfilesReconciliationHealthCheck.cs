using Farm.OrcaSlicer.Worker.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Farm.OrcaSlicer.Worker.Health;

/// <summary>
/// Reports whether the local overlay and caches match shared custom storage.
/// </summary>
public sealed class CustomProfilesReconciliationHealthCheck(
    CustomProfilesReconciliationState state) : IHealthCheck
{
    private readonly CustomProfilesReconciliationState _state =
        state ?? throw new ArgumentNullException(nameof(state));

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (_state.IsReady)
        {
            return Task.FromResult(
                HealthCheckResult.Healthy(
                    "Custom profile overlay and caches are synchronized."));
        }

        return Task.FromResult(
            HealthCheckResult.Unhealthy(
                _state.Failure
                    ?? "Custom profile reconciliation has not completed."));
    }
}
