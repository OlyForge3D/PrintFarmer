using Farm.Infrastructure.Dtos.DataManagement;

namespace Farm.Infrastructure.Services.DataManagement;

/// <summary>
/// Service for importing database data from JSON format
/// </summary>
public interface IDataImportService
{
    /// <summary>
    /// Import catalog data (manufacturers, models, components) from JSON
    /// </summary>
    /// <param name="catalog">Catalog data to import</param>
    /// <param name="mode">Import mode (Merge or Replace)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Import response with statistics and errors</returns>
    Task<ImportResponseDto> ImportCatalogAsync(CatalogExportDto catalog, ImportMode mode = ImportMode.Merge, CancellationToken ct = default);

    /// <summary>
    /// Import full backup (catalog + printers + locations) from JSON
    /// </summary>
    /// <param name="backup">Full backup data to import</param>
    /// <param name="mode">Import mode (Merge or Replace)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Import response with statistics and errors</returns>
    Task<ImportResponseDto> ImportFullBackupAsync(FullBackupExportDto backup, ImportMode mode = ImportMode.Merge, CancellationToken ct = default);
}
