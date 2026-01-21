using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Services.Maintenance;

/// <summary>
/// Service interface for maintenance alert management and generation.
/// </summary>
public interface IMaintenanceAlertService
{
    /// <summary>
    /// Evaluates all active maintenance schedules for a specific printer
    /// and generates alerts if thresholds are exceeded.
    /// </summary>
    /// <param name="printerId">The printer to evaluate</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of alerts generated</returns>
    Task<int> EvaluatePrinterMaintenanceAsync(
        Guid printerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Acknowledges a maintenance alert (user has seen it).
    /// </summary>
    /// <param name="alertId">The alert ID to acknowledge</param>
    /// <param name="acknowledgedBy">Username who acknowledged</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task AcknowledgeAlertAsync(
        Guid alertId,
        string acknowledgedBy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a maintenance alert (maintenance was completed).
    /// </summary>
    /// <param name="alertId">The alert ID to resolve</param>
    /// <param name="resolvedBy">Username who resolved</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task ResolveAlertAsync(
        Guid alertId,
        string resolvedBy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Dismisses a maintenance alert (user chose to ignore).
    /// </summary>
    /// <param name="alertId">The alert ID to dismiss</param>
    /// <param name="dismissedBy">Username who dismissed</param>
    /// <param name="dismissReason">Reason for dismissal</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DismissAlertAsync(
        Guid alertId,
        string dismissedBy,
        string? dismissReason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all active alerts across all printers.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of active maintenance alerts</returns>
    Task<List<MaintenanceAlert>> GetAllActiveAlertsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all active alerts for a specific printer.
    /// </summary>
    /// <param name="printerId">The printer ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of active maintenance alerts</returns>
    Task<List<MaintenanceAlert>> GetActivePrinterAlertsAsync(
        Guid printerId,
        CancellationToken cancellationToken = default);
}
