using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Maintenance;

/// <summary>
/// Repository interface for PrinterStatistics operations
/// </summary>
public interface IPrinterStatisticsRepository
{
    /// <summary>
    /// Get statistics for a specific printer
    /// </summary>
    Task<PrinterStatistics?> GetByPrinterIdAsync(Guid printerId, CancellationToken ct = default);

    /// <summary>
    /// Get all printer statistics
    /// </summary>
    Task<List<PrinterStatistics>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Create or update printer statistics
    /// </summary>
    Task UpsertAsync(PrinterStatistics statistics, CancellationToken ct = default);

    /// <summary>
    /// Delete statistics for a printer
    /// </summary>
    Task DeleteByPrinterIdAsync(Guid printerId, CancellationToken ct = default);

    /// <summary>
    /// Save changes to the database
    /// </summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
