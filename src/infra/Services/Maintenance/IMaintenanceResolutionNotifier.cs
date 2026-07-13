using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Maintenance;

/// <summary>
/// Publishes transport-level notifications after a maintenance resolution is durably committed.
/// Implementations must isolate individual side-effect failures so one transport cannot suppress
/// the others.
/// </summary>
public interface IMaintenanceResolutionNotifier
{
    /// <summary>
    /// Notifies realtime and webhook consumers that a new completion log was created.
    /// </summary>
    Task NotifyCreatedAsync(
        MaintenanceAlert alert,
        MaintenanceLog log,
        CancellationToken cancellationToken);
}
