using Farm.Infrastructure.Dtos.Maintenance;

namespace Farm.Infrastructure.Services.Maintenance;

/// <summary>
/// Service for exporting and importing maintenance data as JSON.
/// Supports per-entity and full-bundle operations with name-based relationship matching.
/// </summary>
public interface IMaintenanceImportExportService
{
    // ── Export ───────────────────────────────────────────────
    Task<MaintenanceExportEnvelope> ExportComponentsAsync(CancellationToken ct = default);

    Task<MaintenanceExportEnvelope> ExportTasksAsync(CancellationToken ct = default);

    Task<MaintenanceExportEnvelope> ExportPlansAsync(CancellationToken ct = default);

    Task<MaintenanceExportEnvelope> ExportBundleAsync(CancellationToken ct = default);

    // ── Import ───────────────────────────────────────────────
    Task<MaintenanceImportResult> ImportComponentsAsync(List<ComponentExportDto> items, CancellationToken ct = default);

    Task<MaintenanceImportResult> ImportTasksAsync(List<TaskExportDto> items, CancellationToken ct = default);

    Task<MaintenanceImportResult> ImportPlansAsync(List<PlanExportDto> items, CancellationToken ct = default);

    Task<MaintenanceImportResult> ImportBundleAsync(MaintenanceExportEnvelope envelope, CancellationToken ct = default);
}
