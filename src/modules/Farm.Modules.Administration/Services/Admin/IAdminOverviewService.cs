using Farm.Infrastructure.Dtos;

namespace Farm.Modules.Administration.Services.Admin;

/// <summary>
/// Aggregates existing health-check results into a single snapshot for the Admin Control Center.
/// This service composes existing <see cref="Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService"/>
/// results — it does not author a parallel set of probes.
/// </summary>
public interface IAdminOverviewService
{
    /// <summary>
    /// Runs the registered health checks and returns an aggregated overview.
    /// Must degrade gracefully: if any individual subsystem probe fails or times out,
    /// that subsystem is reported as <see cref="SubsystemStatus.Unknown"/> and the response
    /// still returns successfully.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Snapshot of subsystem health and attention items.</returns>
    Task<AdminOverviewDto> GetOverviewAsync(CancellationToken cancellationToken = default);
}
