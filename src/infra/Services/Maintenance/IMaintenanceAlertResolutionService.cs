using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Maintenance;

/// <summary>
/// Atomically resolves a maintenance alert and persists its completion log in a single
/// transactional unit (issue #711, round-7 Finding 5).
/// </summary>
/// <remarks>
/// Closes a TOCTOU hole in the resolve endpoint: previously the completion log was committed
/// (immediate <c>SaveChanges</c>) <em>before</em> the alert mutator re-checked the per-tool
/// maintenance gate. If the gate flipped between the API-side pre-check and the mutator, the log
/// was left persisted while the alert stayed unresolved and the request returned 400. This service
/// re-checks the gate, stages the log, mutates the alert, and commits — or rolls back on any
/// failure — so the log and the alert transition succeed or fail together.
/// </remarks>
public interface IMaintenanceAlertResolutionService
{
    /// <summary>
    /// Resolves the alert and writes its completion log atomically.
    /// </summary>
    /// <param name="alertId">The alert to resolve.</param>
    /// <param name="log">The completion log to persist alongside the resolution.</param>
    /// <param name="resolvedBy">The operator credited with the resolution.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// The committed alert and log, or <see langword="null"/> when the alert no longer exists.
    /// </returns>
    /// <exception cref="PerToolMaintenanceDisabledException">
    /// Thrown when the alert is toolhead-scoped and per-tool maintenance is disabled at commit time.
    /// When thrown, neither the log nor the alert transition is persisted.
    /// </exception>
    Task<MaintenanceAlertResolutionResult?> ResolveWithLogAsync(
        Guid alertId,
        MaintenanceLog log,
        string resolvedBy,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of an atomic resolve-with-log operation.
/// </summary>
/// <param name="Alert">The resolved alert in its committed state.</param>
/// <param name="Log">The persisted completion log.</param>
public sealed record MaintenanceAlertResolutionResult(MaintenanceAlert Alert, MaintenanceLog Log);
