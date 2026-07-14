using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Maintenance;

/// <summary>
/// Raised when a terminal maintenance alert cannot transition to resolved.
/// </summary>
#pragma warning disable CA1032 // Domain exception has one complete, invariant-preserving constructor.
public sealed class MaintenanceAlertNotResolvableException(
    Guid alertId,
    MaintenanceAlertStatus status)
    : InvalidOperationException(
        $"Maintenance alert {alertId} is {status} and cannot be resolved as completed maintenance.")
{
    /// <summary>
    /// Gets the terminal status that prevented resolution.
    /// </summary>
    public MaintenanceAlertStatus Status { get; } = status;
}
#pragma warning restore CA1032
