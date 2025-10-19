using Farm.Infrastructure.Domain;
using Farm.Web.Shared;

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
    Task<Dictionary<Guid, Domain.PrinterCapabilities>> GetCapabilitiesDictionaryAsync(Guid[]? ids, CancellationToken ct);
    Task<List<Domain.PrinterCapabilities>> GetCapabilitiesListAsync(Guid[]? ids, CancellationToken ct);
    Task<Domain.PrinterCapabilities?> GetCapabilitiesByPrinterIdAsync(Guid id, CancellationToken ct);
    Task SaveCapabilitiesAsync(Domain.PrinterCapabilities capabilities, CancellationToken ct);
    // Return printers suitable for export (includes Manufacturer and Model, AsNoTracking).
    Task<List<Printer>> GetPrintersForExportAsync(Guid[]? ids, CancellationToken ct);
    // Quick existence check by name or server URL to avoid duplicates during imports
    Task<bool> ExistsByNameOrServerUrlAsync(string name, string serverUrl, CancellationToken ct);
    // Get total count of printers
    Task<int> CountAsync(CancellationToken ct);
    // Get all printers with a specific backend
    Task<List<Printer>> GetByBackendAsync(PrinterBackend backend, CancellationToken ct);
}
