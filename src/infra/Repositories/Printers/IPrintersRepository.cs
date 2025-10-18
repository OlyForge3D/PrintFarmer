using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Printers;

public interface IPrintersRepository
{
    Task<List<Printer>> GetAllAsync(CancellationToken ct);
    Task<List<Printer>> GetAllWithIncludesAsync(CancellationToken ct);
    Task<Printer?> FindByIdAsync(Guid id, CancellationToken ct);
    Task<Printer?> FindByIdWithIncludesAsync(Guid id, CancellationToken ct);
    Task AddAsync(Printer p, CancellationToken ct);
    Task RemoveAsync(Printer p, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
    Task<Dictionary<Guid, PrinterCapabilities>> GetCapabilitiesDictionaryAsync(Guid[]? ids, CancellationToken ct);
    Task<List<PrinterCapabilities>> GetCapabilitiesListAsync(Guid[]? ids, CancellationToken ct);
    Task<PrinterCapabilities?> GetCapabilitiesByPrinterIdAsync(Guid id, CancellationToken ct);
    Task SaveCapabilitiesAsync(PrinterCapabilities capabilities, CancellationToken ct);
    // Return printers suitable for export (includes Manufacturer and Model, AsNoTracking).
    Task<List<Printer>> GetPrintersForExportAsync(Guid[]? ids, CancellationToken ct);
    // Quick existence check by name or server URL to avoid duplicates during imports
    Task<bool> ExistsByNameOrServerUrlAsync(string name, string serverUrl, CancellationToken ct);
}
