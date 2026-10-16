using System;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Maintenance;

/// <summary>
/// Read model for the printer maintenance-history list endpoint (issue #3297).
/// Projected server-side from <see cref="MaintenanceLog"/> with no change tracking.
/// Property names and order mirror the entity's serialized shape so the wire contract
/// is unchanged: navigations that the list query never loaded (printer, toolhead,
/// deployment, resolved alert) are absent, exactly as they were omitted as nulls before.
/// </summary>
public sealed class MaintenanceLogDto
{
    public Guid Id { get; init; }

    public Guid PrinterId { get; init; }

    public Guid? PrinterMaintenanceScheduleId { get; init; }

    public Guid? ResolvedAlertId { get; init; }

    public Guid? MaintenanceTaskId { get; init; }

    /// <summary>
    /// Catalog task, embedded as before. Materialized untracked by the projection.
    /// </summary>
    public MaintenanceTask? MaintenanceTask { get; init; }

    public Guid? ToolheadId { get; init; }

    public string TaskName { get; init; } = string.Empty;

    public string? Notes { get; init; }

    public string? Component { get; init; }

    public string? PerformedBy { get; init; }

    public DateTime PerformedAt { get; init; }

    public int? DurationMinutes { get; init; }

    public string? PartsReplaced { get; init; }

    public decimal? Cost { get; init; }

    public double? PrinterHoursAtMaintenance { get; init; }

    public double? ToolheadHoursAtMaintenance { get; init; }

    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// Filter for <see cref="IMaintenanceLogRepository.GetPrinterLogsAsync"/>.
/// </summary>
/// <param name="PrinterId">Printer whose history is requested.</param>
/// <param name="PrinterWideOnly">When true, only logs with no toolhead scope are returned.</param>
/// <param name="ToolheadId">When set, only logs scoped to this toolhead are returned.</param>
/// <param name="Limit">Maximum rows to return (most recent first); null returns the full history.</param>
public sealed record MaintenanceLogListQuery(
    Guid PrinterId,
    bool PrinterWideOnly = false,
    Guid? ToolheadId = null,
    int? Limit = null);
