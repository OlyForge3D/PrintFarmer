using Farm.Web.Api.Models.Admin;

namespace Farm.Web.Api.Services.Interfaces;

/// <summary>
/// Service for exporting database data to JSON format
/// </summary>
public interface IDataExportService
{
    /// <summary>
    /// Export catalog data (manufacturers, models, components) as JSON
    /// </summary>
    Task<CatalogExportDto> ExportCatalogAsync(CancellationToken ct = default);

    /// <summary>
    /// Export printer configurations only
    /// </summary>
    Task<List<PrinterExportDto>> ExportPrintersAsync(CancellationToken ct = default);

    /// <summary>
    /// Export full backup (catalog + printers + locations + settings)
    /// </summary>
    Task<FullBackupExportDto> ExportFullBackupAsync(CancellationToken ct = default);
}
