using Farm.Infrastructure;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Printers;

/// <summary>
/// Repository interface for printer configuration persistence and retrieval.
/// Provides CRUD operations for printer entities with related data (manufacturer, model, toolheads).
/// </summary>
public interface IPrintersRepository
{
    /// <summary>
    /// Gets all printers without related entities.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of all printer entities.</returns>
    Task<List<Printer>> GetAllAsync(CancellationToken ct);

    /// <summary>
    /// Gets all printers with related entities (Manufacturer, Model) included.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of printers with includes.</returns>
    Task<List<Printer>> GetAllWithIncludesAsync(CancellationToken ct);

    /// <summary>
    /// Finds a printer by ID without related entities.
    /// </summary>
    /// <param name="id">The printer's unique identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The printer if found, otherwise null.</returns>
    Task<Printer?> FindByIdAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Finds a printer by ID with related entities (Manufacturer, Model) included.
    /// </summary>
    /// <param name="id">The printer's unique identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The printer with includes if found, otherwise null.</returns>
    Task<Printer?> FindByIdWithIncludesAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Adds a new printer to the database.
    /// </summary>
    /// <param name="p">The printer entity to add.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddAsync(Printer p, CancellationToken ct);

    /// <summary>
    /// Removes a printer from the database.
    /// </summary>
    /// <param name="p">The printer entity to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RemoveAsync(Printer p, CancellationToken ct);

    /// <summary>
    /// Detaches a printer entity from EF Core change tracking.
    /// </summary>
    /// <param name="p">The printer entity to detach.</param>
    void Detach(Printer p);

    /// <summary>
    /// Persists pending changes to the database.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task SaveChangesAsync(CancellationToken ct);

    /// <summary>
    /// Gets printers suitable for export with related entities (AsNoTracking).
    /// </summary>
    /// <param name="ids">Optional array of specific printer IDs to export.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of printers with export-ready data.</returns>
    Task<List<Printer>> GetPrintersForExportAsync(Guid[]? ids, CancellationToken ct);

    /// <summary>
    /// Checks if a printer exists with the given name or server URL.
    /// </summary>
    /// <param name="name">The printer name to check.</param>
    /// <param name="serverUrl">The server URL to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if a printer exists with either name or URL.</returns>
    Task<bool> ExistsByNameOrServerUrlAsync(string name, string serverUrl, CancellationToken ct);

    /// <summary>
    /// Gets the total count of printers.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of printers in the database.</returns>
    Task<int> CountAsync(CancellationToken ct);

    /// <summary>
    /// Gets all printers with a specific backend type.
    /// </summary>
    /// <param name="backend">The backend type to filter by.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of printers with the specified backend.</returns>
    Task<List<Printer>> GetByBackendAsync(PrinterBackend backend, CancellationToken ct);

    /// <summary>
    /// Finds a printer by its server URL/IP address.
    /// </summary>
    /// <param name="serverUrl">The server URL to search for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The printer if found, otherwise null.</returns>
    Task<Printer?> FindByServerUrlAsync(string serverUrl, CancellationToken ct);

    /// <summary>
    /// Detaches all tracked entities to prevent concurrent operation errors.
    /// </summary>
    void DetachAllEntities();

    /// <summary>
    /// Gets all printers with Toolheads included, with tracking enabled for template updates.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    Task<List<Printer>> GetAllForTemplateUpdateAsync(CancellationToken ct);

    /// <summary>
    /// Gets a single printer with Toolheads included, with tracking enabled for template updates.
    /// </summary>
    /// <param name="id">The unique identifier of the printer.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<Printer?> FindByIdForTemplateUpdateAsync(Guid id, CancellationToken ct);
}
